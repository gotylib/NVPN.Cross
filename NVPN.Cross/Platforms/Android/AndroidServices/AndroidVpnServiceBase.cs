using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Net;
using Android.OS;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using NVPN.Cross.Dal.Models;
using Process = System.Diagnostics.Process;

namespace NVPN.Cross.Platforms.Android.AndroidServices
{
    [Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
    [IntentFilter([ServiceInterface])]
    public class AndroidVpnServiceBase : VpnService
    {
        private ParcelFileDescriptor? vpnInterface;
        private Process _xrayProcess;
        private bool _isRunning;
        private static string? _tempConfigPath;
        private VlessProfile? _profile;
        private Thread? _vpnThread;
        public override void OnCreate()
        {
            base.OnCreate();
            
            // Создаем канал уведомлений для Android 8.0+
            CreateNotificationChannel();
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel("VPN_CHANNEL", "VPN Service", NotificationImportance.Low)
                {
                    Description = "VPN service notifications",
                    LockscreenVisibility = NotificationVisibility.Private
                };
                
                var notificationManager = GetSystemService(NotificationService) as NotificationManager;
                notificationManager?.CreateNotificationChannel(channel);
            }
        }

        private void StartForegroundService()
        {
            var notification = new Notification.Builder(this, "VPN_CHANNEL")
                .SetContentTitle("MAUI VPN")
                .SetContentText("VPN service is running")
                .SetSmallIcon(Resource.Drawable.maui_splash_image)
                .SetCategory(Notification.CategoryService)
                .SetOngoing(true)
                .Build();
            
            // Используем простой StartForeground без указания типа
            StartForeground(1, notification);
        }
        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            // Диагностика для отладки
            System.Diagnostics.Debug.WriteLine("=== VPN Service Starting ===");
            System.Diagnostics.Debug.WriteLine($"Files Directory: {ApplicationContext.FilesDir?.AbsolutePath}");
            
            // Получаем профиль из Intent
            if (intent?.GetStringExtra("profile") != null)
            {
                _profile = System.Text.Json.JsonSerializer.Deserialize<VlessProfile>(intent.GetStringExtra("profile")!);
            }

            if (_profile == null)
            {
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            // Проверяем разрешения VPN
            var vpnIntent = VpnService.Prepare(this);
            if (vpnIntent != null)
            {
                System.Diagnostics.Debug.WriteLine("VPN permissions not granted - user needs to grant VPN permission");
                System.Diagnostics.Debug.WriteLine($"VPN Intent: {vpnIntent}");
                return StartCommandResult.Sticky;
            }

            // 1. Создаём VPN-интерфейс через Builder
            var builder = new Builder(this);
            builder.SetSession("NVPN")
                   .AddAddress("10.0.0.2", 32)
                   .AddRoute("0.0.0.0", 0)
                   .AddDnsServer("8.8.8.8")
                   .SetMtu(1500);

            System.Diagnostics.Debug.WriteLine("Attempting to establish VPN interface...");
            vpnInterface = builder.Establish();
            
            if (vpnInterface == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: Failed to establish VPN interface - builder.Establish() returned null");
                return StartCommandResult.Sticky;
            }
            
            System.Diagnostics.Debug.WriteLine($"VPN Interface established successfully: {vpnInterface.FileDescriptor?.Handle}");
            
            // Сразу запускаем foreground сервис после создания VPN интерфейса
            StartForegroundService();
            
            // Найти свободный порт, начиная с 10809
            const int startPort = 10809;
            const int maxPort = 10909;
            var selectedPort = startPort; // Используем фиксированный порт для простоты
            
            // На Android используем простой подход - пробуем порты по очереди
            for (var port = startPort; port <= maxPort; port++)
            {
                try
                {
                    using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    selectedPort = port;
                    break;
                }
                catch
                {
                    // Порт занят, пробуем следующий
                    continue;
                }
            }
            
            // 2. Запускаем xray и tun2socks (как процессы)
            // Конфиг для vless
            var config = VlessProfile.GenerateXrayConfig(_profile, selectedPort);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            _tempConfigPath = Path.Combine(Path.GetTempPath(), $"xray_{Guid.NewGuid()}.json");
            File.WriteAllText(_tempConfigPath, json);
            
            // Пробуем найти xray в разных местах
            var xrayExePath = FindXrayExecutable();
            System.Diagnostics.Debug.WriteLine($"Selected xray path: {xrayExePath}");
            
            // Проверяем, что xray файл существует или доступен в PATH
            bool xrayExists = File.Exists(xrayExePath);
            bool xrayAvailable = IsCommandAvailable(xrayExePath);
            
            System.Diagnostics.Debug.WriteLine($"Xray file exists: {xrayExists}");
            System.Diagnostics.Debug.WriteLine($"Xray command available: {xrayAvailable}");
            
            if (!xrayExists && !xrayAvailable)
            {
                System.Diagnostics.Debug.WriteLine($"Cannot find xray executable: {xrayExePath}");
                System.Diagnostics.Debug.WriteLine("Xray not available, VPN service will run without proxy");
                return StartCommandResult.Sticky;
            }
            
            System.Diagnostics.Debug.WriteLine($"Using xray executable: {xrayExePath}");
            
            // Проверяем архитектуру файла перед запуском
            if (File.Exists(xrayExePath))
            {
                IsValidElfFile(xrayExePath);
            }

            var psi = new ProcessStartInfo
            {
                FileName = xrayExePath,
                // Quote config path to handle spaces in paths
                Arguments = $"-c \"{_tempConfigPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            
            try
            {
                _xrayProcess = Process.Start(psi);
                if (_xrayProcess == null)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to start xray process");
                    return StartCommandResult.Sticky;
                }
                
                System.Diagnostics.Debug.WriteLine($"Xray process started successfully with PID: {_xrayProcess.Id}");
            }
            catch (Exception exс)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting xray: {exс.Message}");
                
                // Попробуем альтернативный способ через shell
                System.Diagnostics.Debug.WriteLine("Trying alternative method through shell");
                try
                {
                    System.Diagnostics.Debug.WriteLine($"Attempting to start xray: {xrayExePath}");
                        
                    // Проверяем права еще раз
                    var xrayFile = new Java.IO.File(xrayExePath);
                    if (!xrayFile.CanExecute())
                    {
                        System.Diagnostics.Debug.WriteLine("Xray is not executable, trying to fix permissions");
                        SetExecutablePermissions(xrayExePath);
                    }
                        
                    // Запускаем через shell с явным указанием рабочей директории
                    var shellPsi = new ProcessStartInfo
                    {
                        FileName = "/system/bin/sh",
                        Arguments = $"-c \"cd '{ApplicationContext.FilesDir?.AbsolutePath}' && chmod 755 '{xrayExePath}' && '{xrayExePath}' -c '{_tempConfigPath}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true
                    };
                        
                    _xrayProcess = Process.Start(shellPsi);
                    if (_xrayProcess == null)
                    {
                        System.Diagnostics.Debug.WriteLine("Failed to start xray process via shell");
                        return StartCommandResult.Sticky;
                    }
                        
                    // Ждем немного и проверяем статус
                    Thread.Sleep(3000);
                        
                    if (_xrayProcess.HasExited)
                    {
                        var error = _xrayProcess.StandardError.ReadToEnd();
                        var output = _xrayProcess.StandardOutput.ReadToEnd();
                        System.Diagnostics.Debug.WriteLine($"Xray process exited immediately. Exit code: {_xrayProcess.ExitCode}");
                        System.Diagnostics.Debug.WriteLine($"Xray stderr: {error}");
                        System.Diagnostics.Debug.WriteLine($"Xray stdout: {output}");
                        return StartCommandResult.Sticky;
                    }
                        
                    System.Diagnostics.Debug.WriteLine($"Xray process started successfully with PID: {_xrayProcess.Id}");
                        
                    // Запускаем мониторинг вывода xray в отдельном потоке
                    new Thread(() =>
                        {
                            try
                            {
                                while (!_xrayProcess.HasExited && _isRunning)
                                {
                                    var line = _xrayProcess.StandardOutput.ReadLine();
                                    if (!string.IsNullOrEmpty(line))
                                        System.Diagnostics.Debug.WriteLine($"Xray: {line}");
                                    
                                    var errorLine = _xrayProcess.StandardError.ReadLine();
                                    if (!string.IsNullOrEmpty(errorLine))
                                        System.Diagnostics.Debug.WriteLine($"Xray ERROR: {errorLine}");
                                    
                                    Thread.Sleep(100);
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Xray monitor error: {ex.Message}");
                            }
                        })
                        { IsBackground = true }.Start();
                        
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error starting xray: {ex.Message}");
                    return StartCommandResult.Sticky;
                }
            }
            

            // Запускаем xray для SOCKS прокси
            _isRunning = true;

            // Запускаем поток для обработки VPN трафика
            _vpnThread = new Thread(ProcessVpnTraffic)
            {
                IsBackground = true,
                Name = "VPN Traffic Processor"
            };
            _vpnThread.Start();

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            _isRunning = false;
            
            // Ждем завершения потока
            _vpnThread?.Join(1000);
            
            vpnInterface?.Close();
            vpnInterface = null;
            
            try { _xrayProcess?.Kill(); }
            catch
            {
                // ignored
            }

            // Удаляем временный конфиг
            if (!string.IsNullOrEmpty(_tempConfigPath) && File.Exists(_tempConfigPath))
            {
                try { File.Delete(_tempConfigPath); }
                catch { /* ignored */ }
            }

            base.OnDestroy();
        }

        private void ProcessVpnTraffic()
        {
            try
            {
                using var vpnFile = new Java.IO.FileInputStream(vpnInterface.FileDescriptor);
                using var vpnChannel = vpnFile.Channel;
        
                var socksClient = new SocksClient("127.0.0.1", 10809);
                var buffer = Java.Nio.ByteBuffer.Allocate(32767);
        
                while (_isRunning)
                {
                    try
                    {
                        buffer.Clear();
                        var bytesRead = vpnChannel.Read(buffer);
                
                        if (bytesRead > 0)
                        {
                            buffer.Flip();
                            var data = new byte[buffer.Remaining()];
                            buffer.Get(data);
                    
                            // Проксируем через SOCKS
                            var proxiedData = socksClient.ProxyData(data, data.Length);
                    
                            if (proxiedData != null && proxiedData.Length > 0)
                            {
                                var outputBuffer = Java.Nio.ByteBuffer.Wrap(proxiedData);
                                while (outputBuffer.HasRemaining)
                                {
                                    vpnChannel.Write(outputBuffer);
                                }
                            }
                        }
                        else
                        {
                            Thread.Sleep(10);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"VPN Traffic Error: {ex.Message}");
                        if (!_isRunning) break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VPN Process Error: {ex.Message}");
            }
        }

        private void ProcessVpnTrafficWithStreams(FileStream inputStream, FileStream outputStream, SocksClient socksClient)
        {
            var buffer = new byte[4096];
            
            System.Diagnostics.Debug.WriteLine("SOCKS client created for 127.0.0.1:10809");
            System.Diagnostics.Debug.WriteLine("Starting VPN traffic processing loop...");

            while (_isRunning)
            {
                try
                {
                    // Читаем данные из VPN интерфейса
                    var bytesRead = inputStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"VPN: Received {bytesRead} bytes from interface");
                        
                        // Анализируем IP пакет
                        if (IsValidIpPacket(buffer, bytesRead))
                        {
                            System.Diagnostics.Debug.WriteLine($"VPN: Valid IP packet detected, proxying through SOCKS");
                            
                            // Проксируем через SOCKS
                            var proxiedData = socksClient.ProxyData(buffer, bytesRead);
                            if (proxiedData != null && proxiedData.Length > 0)
                            {
                                outputStream.Write(proxiedData, 0, proxiedData.Length);
                                outputStream.Flush();
                                System.Diagnostics.Debug.WriteLine($"VPN: Sent {proxiedData.Length} bytes back to interface");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("VPN: SOCKS proxy returned no data");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("VPN: Invalid IP packet received");
                        }
                    }
                    else
                    {
                        // Небольшая задержка если нет данных
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"VPN Traffic Error: {ex.Message}");
                    break;
                }
            }
        }

        private void ProcessVpnTrafficWithSingleStream(FileStream vpnStream, SocksClient socksClient)
        {
            var buffer = new byte[4096];
            
            System.Diagnostics.Debug.WriteLine("SOCKS client created for 127.0.0.1:10809");
            System.Diagnostics.Debug.WriteLine("Starting VPN traffic processing loop (single stream)...");

            while (_isRunning)
            {
                try
                {
                    // Читаем данные из VPN интерфейса
                    var bytesRead = vpnStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"VPN: Received {bytesRead} bytes from interface");
                        
                        // Анализируем IP пакет
                        if (IsValidIpPacket(buffer, bytesRead))
                        {
                            System.Diagnostics.Debug.WriteLine($"VPN: Valid IP packet detected, proxying through SOCKS");
                            
                            // Проксируем через SOCKS
                            var proxiedData = socksClient.ProxyData(buffer, bytesRead);
                            if (proxiedData != null && proxiedData.Length > 0)
                            {
                                vpnStream.Write(proxiedData, 0, proxiedData.Length);
                                vpnStream.Flush();
                                System.Diagnostics.Debug.WriteLine($"VPN: Sent {proxiedData.Length} bytes back to interface");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("VPN: SOCKS proxy returned no data");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("VPN: Invalid IP packet received");
                        }
                    }
                    else
                    {
                        // Небольшая задержка если нет данных
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"VPN Traffic Error: {ex.Message}");
                    break;
                }
            }
        }

        private bool IsValidIpPacket(byte[] data, int length)
        {
            if (length < 20) return false; // Минимальный размер IP заголовка
            
            // Проверяем версию IP (4 или 6)
            var version = (data[0] >> 4) & 0x0F;
            return version == 4 || version == 6;
        }

        private string FindXrayExecutable()
        {
            // Сначала пробуем найти уже извлеченный xray
            var extractedXrayPath = Path.Combine(ApplicationContext.FilesDir?.AbsolutePath ?? "", "xray");
            if (File.Exists(extractedXrayPath))
            {
                System.Diagnostics.Debug.WriteLine($"Found extracted xray at: {extractedXrayPath}");
                return extractedXrayPath;
            }
            
            // Если не найден, пытаемся извлечь из APK
            var extractedPath = ExtractXrayFromAssets();
            if (!string.IsNullOrEmpty(extractedPath) && File.Exists(extractedPath))
            {
                System.Diagnostics.Debug.WriteLine($"Successfully extracted xray to: {extractedPath}");
                return extractedPath;
            }
            
            // Fallback: поиск в других местах
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Xray", "Android", "xray"),
                Path.Combine(ApplicationContext.FilesDir?.AbsolutePath ?? "", "xray"),
                Path.Combine(ApplicationContext.PackageCodePath ?? "", "xray"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xray"),
                "xray"
            };

            foreach (var path in possiblePaths)
            {
                System.Diagnostics.Debug.WriteLine($"Checking xray path: {path}");
                if (File.Exists(path))
                {
                    System.Diagnostics.Debug.WriteLine($"Found xray at: {path}");
                    return path;
                }
            }

            System.Diagnostics.Debug.WriteLine("Xray not found in any of the expected locations");
            return "xray";
        }

        private string ExtractXrayFromAssets()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Attempting to extract xray from APK assets");
                
                var outputPath = Path.Combine(ApplicationContext.FilesDir?.AbsolutePath ?? "", "xray");
                
                // Удаляем старый файл если существует
                if (File.Exists(outputPath))
                {
                    try { File.Delete(outputPath); }
                    catch { /* ignored */ }
                }
                
                // Проверяем, что папка существует
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
                
                // Список возможных путей в assets
                var assetPaths = new[]
                {
                    "Xray/Android/xray",
                    "xray/Android/xray", 
                    "xray",
                    "Resources/Xray/Android/xray",
                    "Resources/xray/Android/xray"
                };
                
                foreach (var assetPath in assetPaths)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"Trying asset path: {assetPath}");
                        
                        using var inputStream = Assets.Open(assetPath);
                        if (inputStream != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Found xray in assets at {assetPath}, extracting to: {outputPath}");
                            
                            using (var outputStream = File.Create(outputPath))
                            {
                                inputStream.CopyTo(outputStream);
                                outputStream.Flush();
                            }
                            
                            // Даем время файлу записаться
                            Thread.Sleep(200);
                            
                            var fileInfo = new FileInfo(outputPath);
                            System.Diagnostics.Debug.WriteLine($"Successfully extracted xray ({fileInfo.Length} bytes)");
                            
                            if (fileInfo.Length > 0)
                            {
                                // Устанавливаем права на выполнение
                                if (SetExecutablePermissions(outputPath))
                                {
                                    // Проверяем, что файл действительно исполняемый
                                    if (IsValidElfFile(outputPath))
                                    {
                                        return outputPath;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error trying asset path {assetPath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting xray from assets: {ex.Message}");
            }
            
            return string.Empty;
        }

        private bool IsValidElfFile(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                var buffer = new byte[24]; // Читаем больше байт для полного заголовка
                var bytesRead = fs.Read(buffer, 0, 24);
                
                if (bytesRead < 16)
                {
                    System.Diagnostics.Debug.WriteLine($"File too small for ELF header: {bytesRead} bytes");
                    return false;
                }
                
                // Проверяем ELF magic
                if (buffer[0] != 0x7F || buffer[1] != 0x45 || buffer[2] != 0x4C || buffer[3] != 0x46)
                {
                    System.Diagnostics.Debug.WriteLine("File does not have ELF magic header");
                    return false;
                }
                
                // Проверяем архитектуру (5-й байт: 1 = 32-bit, 2 = 64-bit)
                var is64bit = buffer[4] == 2;
                System.Diagnostics.Debug.WriteLine($"ELF file is {(is64bit ? "64-bit" : "32-bit")}");
                
                // Проверяем архитектуру
                // В 32-bit ELF: machine находится в байте 18
                // В 64-bit ELF: machine находится в байте 18
                byte machine;
                if (bytesRead >= 19)
                {
                    machine = buffer[18];
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("File too small to read machine architecture");
                    return false;
                }
                
                string arch = machine switch
                {
                    0x28 => "ARM",
                    0x3E => "x86_64", 
                    0xB7 => "AArch64",
                    _ => $"Unknown (0x{machine:X2})"
                };
                
                System.Diagnostics.Debug.WriteLine($"ELF file architecture: {arch}");
                
                // Проверяем совместимость архитектуры
                if (machine == 0x3E) // x86_64
                {
                    System.Diagnostics.Debug.WriteLine("ELF file is compatible with x86_64 emulator");
                    return true;
                }
                else if (machine == 0xB7) // AArch64
                {
                    System.Diagnostics.Debug.WriteLine($"ELF file architecture {arch} - will try to run anyway");
                    return true; // Пробуем запустить даже если архитектура не совпадает
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"ELF file architecture {arch} - unknown compatibility");
                    return true; // Пробуем запустить
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking ELF file: {ex.Message}");
                return false;
            }
        }

        private bool SetExecutablePermissions(string filePath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Setting executable permissions for: {filePath}");
                
                // Метод 1: Через Runtime с проверкой результата
                try
                {
                    var process = Java.Lang.Runtime.GetRuntime().Exec(new[] { "chmod", "755", filePath });
                    process.WaitFor(); // Ждем завершения
                    
                    var exitCode = process.ExitValue();
                    if (exitCode == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"Successfully set permissions via Runtime: {filePath}");
                        
                        // Проверяем, что права установились
                        var file = new Java.IO.File(filePath);
                        if (file.CanExecute())
                        {
                            System.Diagnostics.Debug.WriteLine("File is now executable");
                            return true;
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"Runtime chmod failed with exit code: {exitCode}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Runtime chmod failed: {ex.Message}");
                }
                
                // Метод 2: Попробуем через Process с полным путем к chmod
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "/system/bin/chmod",
                        Arguments = $"755 \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        process.WaitForExit(5000);
                        if (process.ExitCode == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"Successfully set permissions via Process: {filePath}");
                            return true;
                        }
                        else
                        {
                            var error = process.StandardError.ReadToEnd();
                            System.Diagnostics.Debug.WriteLine($"Process chmod failed: {error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Process chmod failed: {ex.Message}");
                }
                
                // Метод 3: Попробуем скопировать и установить права по-другому
                try
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), "xray_temp");
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    
                    File.Copy(filePath, tempPath);
                    File.Delete(filePath);
                    File.Move(tempPath, filePath);
                    
                    // Еще одна попытка
                    Java.Lang.Runtime.GetRuntime().Exec(new[] { "chmod", "700", filePath }).WaitFor();
                    
                    var file = new Java.IO.File(filePath);
                    if (file.CanExecute())
                    {
                        System.Diagnostics.Debug.WriteLine("File is executable after copy method");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Copy method failed: {ex.Message}");
                }
                
                System.Diagnostics.Debug.WriteLine("All permission setting methods failed");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting executable permissions: {ex.Message}");
                return false;
            }
        }

        private bool IsCommandAvailable(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
            return false;
        }

        private void StartProcess(ref Process proc, string file, string args)
        {
            if (!File.Exists(file)) return;
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            proc = Process.Start(psi);
        }
    }

    // Простой SOCKS клиент для проксирования трафика
    public class SocksClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient? _client;
        private NetworkStream? _stream;

        public SocksClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public byte[]? ProxyData(byte[] data, int length)
        {
            try
            {
                if (_client == null || !_client.Connected)
                {
                    _client = new TcpClient();
                    _client.Connect(_host, _port);
                    _stream = _client.GetStream();
                }

                if (_stream != null)
                {
                    _stream.Write(data, 0, length);
                    
                    var response = new byte[4096];
                    var bytesRead = _stream.Read(response, 0, response.Length);
                    
                    if (bytesRead > 0)
                    {
                        var result = new byte[bytesRead];
                        Array.Copy(response, result, bytesRead);
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SOCKS Error: {ex.Message}");
            }

            return null;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
    }
}
