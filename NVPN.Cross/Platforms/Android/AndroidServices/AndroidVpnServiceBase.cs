using Android.App;
using Android.Content;
using Android.Content.PM;
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
                .SetSmallIcon(Resource.Drawable.notification_action_background)
                .SetCategory(Notification.CategoryService)
                .SetOngoing(true)
                .Build();
            
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                // Для Android 10+ используем специальный тип для VPN
                StartForeground(1, notification, ForegroundService.TypeSpecialUse);
            }
            else
            {
                StartForeground(1, notification);
            }
        }
        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
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

            // 1. Создаём VPN-интерфейс через Builder
            var builder = new Builder(this);
            builder.SetSession("NVPN")
                   .AddAddress("10.0.0.2", 32)
                   .AddRoute("0.0.0.0", 0)
                   .AddDnsServer("8.8.8.8")
                   .SetMtu(1500);

            vpnInterface = builder.Establish();
            
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
            
            // Проверяем, что xray файл существует или доступен в PATH
            if (!File.Exists(xrayExePath) && !IsCommandAvailable(xrayExePath))
            {
                System.Diagnostics.Debug.WriteLine($"Cannot find xray executable: {xrayExePath}");
                // Для тестирования создаем заглушку
                System.Diagnostics.Debug.WriteLine("Xray not available, VPN service will run without proxy");
                return StartCommandResult.Sticky;
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting xray: {ex.Message}");
                return StartCommandResult.Sticky;
            }
            

            // Запускаем xray для SOCKS прокси
            _isRunning = true;

            // Запускаем foreground сервис
            StartForegroundService();

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
            if (vpnInterface == null || _profile == null) return;

            try
            {
                using var inputStream = new FileStream(vpnInterface.FileDescriptor.Handle, FileAccess.ReadWrite);
                var buffer = new byte[4096];
                var socksClient = new SocksClient("127.0.0.1", 10809); // xray SOCKS порт

                while (_isRunning)
                {
                    try
                    {
                        // Читаем данные из VPN интерфейса
                        var bytesRead = inputStream.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            // Анализируем IP пакет
                            if (IsValidIpPacket(buffer, bytesRead))
                            {
                                // Проксируем через SOCKS
                                var proxiedData = socksClient.ProxyData(buffer, bytesRead);
                                if (proxiedData != null && proxiedData.Length > 0)
                                {
                                    inputStream.Write(proxiedData, 0, proxiedData.Length);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"VPN Traffic Error: {ex.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VPN Process Error: {ex.Message}");
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
            // Список возможных путей к xray для Android
            var possiblePaths = new[]
            {
                // Основной путь в output directory
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Xray", "Android", "xray"),
                // Альтернативные пути
                Path.Combine(ApplicationContext.FilesDir?.AbsolutePath ?? "", "xray"),
                Path.Combine(ApplicationContext.PackageCodePath ?? "", "xray"),
                // Прямой путь к файлу в сборке
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xray"),
                "xray" // Попробуем найти в PATH
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
            // Если не нашли, возвращаем последний вариант (для поиска в PATH)
            return "xray";
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
