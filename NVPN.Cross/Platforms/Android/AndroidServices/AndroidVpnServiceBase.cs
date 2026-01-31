using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using NVPN.Cross.Dal.Models;
using System.Diagnostics;
using System.Text.Json;
using Debug = System.Diagnostics.Debug;
using Process = System.Diagnostics.Process;
using SysException = System.Exception;
using SysFile = System.IO.File;
using SysIO = System.IO;
using SysThread = System.Threading.Thread;

namespace NVPN.Cross.Platforms.Android.AndroidServices
{
    [Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
    [IntentFilter([ServiceInterface])]
    public class AndroidVpnServiceBase : VpnService
    {
        private const string NOTIFICATION_CHANNEL_ID = "VPN_CHANNEL";
        private const int NOTIFICATION_ID = 1001;

        /// <summary>Если true — пробовать внешний tun2socks. xjasonlyu с /proc даёт "permission denied" на Android — используем C#.</summary>
        public static bool UseExternalTun2Socks { get; set; } = true;

        /// <summary>Резервное значение UseSocksTunMode (на случай, если Preferences в Blazor не успевает сохраниться).</summary>
        public static bool UseSocksTunModeOverride { get; set; }

        private ParcelFileDescriptor? _vpnInterface;
        private Process? _xrayProcess;
        private Process? tun2socksProcess;
        private bool _isRunning;
        private string? _tempConfigPath;
        private VlessProfile? _profile;
        private Thread? _monitoringThread;
        private volatile bool _monitoringActive = false;
        private volatile bool _useHevTun2Socks;

        public override void OnCreate()
        {
            base.OnCreate();
            CreateNotificationChannel();
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(NOTIFICATION_CHANNEL_ID, "VPN Service", NotificationImportance.Default)
                {
                    Description = "VPN connection status",
                    LockscreenVisibility = NotificationVisibility.Private
                };

                var notificationManager = GetSystemService(NotificationService) as NotificationManager;
                notificationManager?.CreateNotificationChannel(channel);
            }
        }

        private Notification CreateNotification(string title, string text)
        {
            var builder = new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))
                .SetSmallIcon(Resource.Drawable.maui_splash_image)
                .SetCategory(NotificationCompat.CategoryService)
                .SetOngoing(true)
                .SetPriority(NotificationCompat.PriorityDefault);

            return builder.Build();
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            Debug.WriteLine("=== VPN Service Starting ===");

            // КРИТИЧЕСКИ ВАЖНО: Вызываем StartForeground() ПЕРВЫМ ДЕЛОМ
            // Android требует вызвать это в течение 5 секунд после startForegroundService()
            var notification = CreateNotification("VPN", "Инициализация...");

            // Для Android 14+ (API 34+) ОБЯЗАТЕЛЬНО указываем тип foreground service
            // Используем ConnectedDevice - официальный тип для VPN сервисов
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake) // API 34
            {
                // ForegroundService.TypeConnectedDevice = 16 (0x10)
                // Это официальный тип для VPN согласно документации Android
                const int TYPE_CONNECTED_DEVICE = 16;
                StartForeground(NOTIFICATION_ID, notification, (global::Android.Content.PM.ForegroundService)TYPE_CONNECTED_DEVICE);
            }
            else
            {
                StartForeground(NOTIFICATION_ID, notification);
            }

            Debug.WriteLine("Foreground service started successfully");

            try
            {
                // Получаем профиль из Intent
                if (intent?.GetStringExtra("profile") != null)
                {
                    _profile = JsonSerializer.Deserialize<VlessProfile>(intent.GetStringExtra("profile")!);
                }

                if (_profile == null)
                {
                    Debug.WriteLine("ERROR: No profile provided");
                    StopSelf();
                    return StartCommandResult.NotSticky;
                }

                Debug.WriteLine($"Profile loaded: {_profile.Address}:{_profile.Port}");

                // Запускаем VPN в отдельном потоке
                new SysThread(StartVpnConnection) { IsBackground = true }.Start();

                return StartCommandResult.Sticky;
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"ERROR in OnStartCommand: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                StopSelf();
                return StartCommandResult.NotSticky;
            }
        }

        private void StartVpnConnection()
        {
            try
            {
                Debug.WriteLine("Starting VPN connection in background thread");

                // Создаём VPN-интерфейс
                var builder = new Builder(this);
                builder.SetSession("NVPN")
                       .AddAddress("10.0.0.2", 24)
                       .AddRoute("0.0.0.0", 0)
                       .AddDnsServer("8.8.8.8")
                       .AddDnsServer("1.1.1.1")
                       .SetBlocking(true)
                       .SetMtu(1500);

                // КРИТИЧНО: Исключаем наше приложение из VPN!
                // Иначе Xray (дочерний процесс) не сможет подключиться к серверу VLESS —
                // его трафик пойдёт в TUN → tun2socks → SOCKS(Xray) → мёртвая петля.
                try
                {
                    builder.AddDisallowedApplication(PackageName ?? "com.companyname.nvpn.cross");
                    Debug.WriteLine($"✓ Excluded app from VPN (Xray will use real network): {PackageName}");
                }
                catch (Java.Lang.Exception ex)
                {
                    Debug.WriteLine($"WARNING: Could not exclude app from VPN: {ex.Message}");
                }

                Debug.WriteLine("Establishing VPN interface...");
                _vpnInterface = builder.Establish();

                if (_vpnInterface == null)
                {
                    Debug.WriteLine("ERROR: Failed to establish VPN interface");
                    MainThread.BeginInvokeOnMainThread(() => StopSelf());
                    return;
                }

                Debug.WriteLine($"VPN Interface established: fd={_vpnInterface.Fd}");

                // ОБЯЗАТЕЛЬНО: Немедленно получаем FD, пока он не закрыт
                int tunFd = -1;
                try
                {
                    tunFd = _vpnInterface.DetachFd();
                    Debug.WriteLine($"✓ Detached FD: {tunFd}");

                    if (tunFd <= 0)
                    {
                        Debug.WriteLine($"ERROR: Invalid FD after detach: {tunFd}");
                        MainThread.BeginInvokeOnMainThread(() => StopSelf());
                        return;
                    }
                }
                catch (Java.Lang.IllegalStateException ex)
                {
                    Debug.WriteLine($"ERROR: FD already closed! {ex.Message}");

                    // Пробуем альтернативный способ: получаем FD через отражение
                    if (tunFd <= 0)
                    {
                        MainThread.BeginInvokeOnMainThread(() => StopSelf());
                        return;
                    }
                }

                // Обновляем уведомление
                UpdateNotification("VPN", "Запуск прокси...");

                // Запускаем Xray
                if (!StartXrayProcess())
                {
                    Debug.WriteLine("ERROR: Failed to start Xray process");
                    MainThread.BeginInvokeOnMainThread(() => StopSelf());
                    return;
                }

                // Даем Xray время запуститься
                SysThread.Sleep(2000);

                _isRunning = true;

                var cts = new CancellationTokenSource();

                // 0) Приоритет: libhev-socks5-tunnel (in-process, обходит SELinux)
                if (StartHevTun2Socks(tunFd, cts))
                {
                    _useHevTun2Socks = true;
                    UpdateNotification("VPN подключен", $"Сервер: {_profile?.Address} (hev)");
                }
                
                Debug.WriteLine("VPN connection established successfully");

                // Простая loop для поддержания сервиса активным
                while (_isRunning)
                {
                    SysThread.Sleep(1000);

                    // Проверяем, жив ли xray процесс
                    if (_xrayProcess?.HasExited == true)
                    {
                        Debug.WriteLine("Xray process has exited unexpectedly");
                        break;
                    }
                }
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"ERROR in StartVpnConnection: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                MainThread.BeginInvokeOnMainThread(() => StopSelf());
            }

        }

        /// <summary>Запускает libhev-socks5-tunnel in-process (TUN FD напрямую, обход SELinux).</summary>
        private static bool StartHevTun2Socks(int tunFd, CancellationTokenSource cts)
        {
            if (!HevSocks5Tunnel.IsAvailable)
            {
                Debug.WriteLine("hev-socks5-tunnel: library not found, skipping");
                return false;
            }
            try
            {
                var runThread = new SysThread(() =>
                {
                    try
                    {
                        Debug.WriteLine("=== HevSocks5Tunnel.Run started (127.0.0.1:10809) ===");
                        int r = HevSocks5Tunnel.Run(tunFd, "127.0.0.1", 10809);
                        Debug.WriteLine($"HevSocks5Tunnel exited with code {r}");
                    }
                    catch (SysException ex)
                    {
                        Debug.WriteLine($"HevSocks5Tunnel error: {ex.Message}");
                    }
                }) { IsBackground = true };
                runThread.Start();
                SysThread.Sleep(500);
                if (!runThread.IsAlive)
                {
                    Debug.WriteLine("HevSocks5Tunnel thread died immediately");
                    return false;
                }
                Debug.WriteLine("✓ HevSocks5Tunnel started");
                return true;
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"HevSocks5Tunnel start error: {ex.Message}");
                return false;
            }
        }
        private bool StartXrayProcess(string? socksListen = null)
        {
            try
            {
                Debug.WriteLine("Starting Xray process...");

                // Находим свободный порт
                const int socksPort = 10809;

                // КРИТИЧЕСКИ ВАЖНО: Извлекаем geo-файлы в ту же директорию, где будет конфиг
                var geoDir = ExtractGeoFilesFromAssets();

                // Генерируем конфиг С путями к geo-файлам (обычный режим с SOCKS)
                Debug.WriteLine($"DEBUG: geoDir passed to GenerateXrayConfig: '{geoDir}', socksListen: {socksListen ?? "127.0.0.1"}");
                var config = VlessProfile.GenerateXrayConfig(_profile!, socksPort, geoDir, socksListen);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

                _tempConfigPath = Path.Combine(CacheDir?.AbsolutePath ?? Path.GetTempPath(), $"xray_config_{Guid.NewGuid()}.json");
                SysFile.WriteAllText(_tempConfigPath, json);

                Debug.WriteLine($"Config written to: {_tempConfigPath}");
                Debug.WriteLine($"Config content (first 1000 chars): {(json.Length > 1000 ? json.Substring(0, 1000) : json)}");

                // Находим xray executable
                var xrayPath = ExtractXrayFromAssets();
                if (string.IsNullOrEmpty(xrayPath) || !SysFile.Exists(xrayPath))
                {
                    Debug.WriteLine($"ERROR: Xray executable not found at: {xrayPath}");
                    return false;
                }

                Debug.WriteLine($"Using Xray at: {xrayPath}");

                // Проверяем размер перед запуском
                var xrayFileInfo = new FileInfo(xrayPath);
                Debug.WriteLine($"Xray file size: {xrayFileInfo.Length} bytes");

                // УБРАНО: Не нужно chmod для файлов в NativeLibraryDir - они уже executable
                // SetExecutablePermissions(xrayPath); 

                // Запускаем xray напрямую (не через shell - файл уже executable)
                var psi = new ProcessStartInfo
                {
                    FileName = xrayPath,
                    Arguments = $"run -c \"{_tempConfigPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                // КРИТИЧЕСКИ ВАЖНО: Устанавливаем переменную окружения для Xray
                psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = geoDir;
                Debug.WriteLine($"Set XRAY_LOCATION_ASSET={geoDir}");

                Debug.WriteLine($"Starting command: {psi.FileName} {psi.Arguments}");

                _xrayProcess = Process.Start(psi);

                if (_xrayProcess == null)
                {
                    Debug.WriteLine("ERROR: Failed to start Xray process");
                    return false;
                }

                Debug.WriteLine($"Xray process started with PID: {_xrayProcess.Id}");

                // Ждем 5 секунд, чтобы Xray успел полностью стартовать и поднять SOCKS5 сервер
                Debug.WriteLine("Waiting for Xray to initialize...");
                SysThread.Sleep(5000);

                if (_xrayProcess.HasExited)
                {
                    var error = _xrayProcess.StandardError.ReadToEnd();
                    var output = _xrayProcess.StandardOutput.ReadToEnd();
                    Debug.WriteLine($"ERROR: Xray exited immediately. Exit code: {_xrayProcess.ExitCode}");
                    Debug.WriteLine($"Stderr: {error}");
                    Debug.WriteLine($"Stdout: {output}");
                    return false;
                }

                // Запускаем мониторинг вывода
                StartMonitoringThread();

                Debug.WriteLine("Xray process started successfully");
                return true;
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"ERROR starting Xray: {ex.Message}");
                return false;
            }
        }
      
        private void StartMonitoringThread()
        {
            if (_monitoringThread != null && _monitoringThread.IsAlive)
                return;

            _monitoringActive = true;

            _monitoringThread = new SysThread(() =>
              {
                Debug.WriteLine("Monitoring thread started");

                while (_monitoringActive)
                {
                    try
                    {
                        Thread.Sleep(5000);

                        // Проверяем Xray
                        if (_xrayProcess?.HasExited == true)
                        {
                            Debug.WriteLine("[MONITOR] Xray died");
                            MainThread.BeginInvokeOnMainThread(() => StopSelf());
                            break;
                        }

                        // Проверяем tun2socks
                        if (tun2socksProcess?.HasExited == true)
                        {
                            Debug.WriteLine("[MONITOR] tun2socks died");
                            MainThread.BeginInvokeOnMainThread(() => StopSelf());
                            break;
                        }

                        Debug.WriteLine("[MONITOR] All processes are running");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MONITOR ERROR] {ex.Message}");
                    }
                }

                Debug.WriteLine("Monitoring thread stopped");
            })
            {
                IsBackground = true,
                Name = "VPN-Monitor"
            };

            _monitoringThread.Start();
        }

        private void StopMonitoringThread()
        {
            _monitoringActive = false;
            _monitoringThread?.Interrupt();

            if (_monitoringThread != null && _monitoringThread.IsAlive)
            {
                if (!_monitoringThread.Join(3000))
                {
                    Debug.WriteLine("WARNING: Monitoring thread didn't stop gracefully");
                }
            }

            _monitoringThread = null;
        }

        private string ExtractXrayFromAssets()
        {
            try
            {
                // КРИТИЧЕСКИ ВАЖНО: На Android 10+ используем NativeLibraryDir
                // Только файлы из lib/ директории могут быть выполнены
                var nativeLibDir = ApplicationContext?.ApplicationInfo?.NativeLibraryDir;
                if (string.IsNullOrEmpty(nativeLibDir))
                {
                    Debug.WriteLine("ERROR: Could not get NativeLibraryDir");
                    return string.Empty;
                }

                // Бинарник упакован как libxray.so
                var xrayPath = Path.Combine(nativeLibDir, "libxray.so");
                Debug.WriteLine($"Looking for Xray at: {xrayPath}");

                if (!SysFile.Exists(xrayPath))
                {
                    Debug.WriteLine($"ERROR: Xray not found at: {xrayPath}");
                    return string.Empty;
                }

                var fileInfo = new FileInfo(xrayPath);
                Debug.WriteLine($"✓ Found Xray: {fileInfo.Length} bytes at {xrayPath}");

                return xrayPath;
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"ERROR in ExtractXrayFromAssets: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return string.Empty;
            }
        }

        private string ExtractGeoFilesFromAssets()
        {
            try
            {
                // КРИТИЧЕСКИ ВАЖНО: Извлекаем geo-файлы прямо в cache директорию (не в поддиректорию!)
                // Xray ищет их относительно конфиг-файла
                var geoDir = CacheDir?.AbsolutePath ?? Path.GetTempPath();

                Debug.WriteLine($"Extracting geo files to: {geoDir}");

                // Пробуем разные пути в assets
                string[] possiblePaths = new[] { "geoip.dat", "Android/geoip.dat", "Xray/Android/geoip.dat" };

                // Извлекаем geoip.dat
                var geoipPath = Path.Combine(geoDir, "geoip.dat");
                if (!SysFile.Exists(geoipPath))
                {
                    bool extracted = false;
                    foreach (var assetPath in possiblePaths)
                    {
                        try
                        {
                            Debug.WriteLine($"Trying to open: {assetPath}");
                            using var geoipStream = ApplicationContext?.Assets?.Open(assetPath);
                            using var geoipFile = SysFile.Create(geoipPath);
                            geoipStream?.CopyTo(geoipFile);
                            Debug.WriteLine($"✓ Extracted geoip.dat from {assetPath}: {new FileInfo(geoipPath).Length} bytes");
                            extracted = true;
                            break;
                        }
                        catch (SysException ex)
                        {
                            Debug.WriteLine($"Failed to extract from '{assetPath}': {ex.Message}");
                        }
                    }

                    if (!extracted)
                    {
                        Debug.WriteLine("ERROR: Could not extract geoip.dat from any path");
                        return string.Empty;
                    }
                }

                // Извлекаем geosite.dat
                var geositePath = Path.Combine(geoDir, "geosite.dat");
                if (!SysFile.Exists(geositePath))
                {
                    bool extracted = false;
                    possiblePaths = ["geosite.dat", "Android/geosite.dat", "Xray/Android/geosite.dat"];

                    foreach (var assetPath in possiblePaths)
                    {
                        try
                        {
                            Debug.WriteLine($"Trying to open: {assetPath}");
                            using var geositeStream = ApplicationContext?.Assets?.Open(assetPath);
                            using var geositeFile = SysFile.Create(geositePath);
                            geositeStream?.CopyTo(geositeFile);
                            Debug.WriteLine($"✓ Extracted geosite.dat from {assetPath}: {new FileInfo(geositePath).Length} bytes");
                            extracted = true;
                            break;
                        }
                        catch (SysException ex)
                        {
                            Debug.WriteLine($"Failed to extract from '{assetPath}': {ex.Message}");
                        }
                    }

                    if (!extracted)
                    {
                        Debug.WriteLine("ERROR: Could not extract geosite.dat from any path");
                        return string.Empty;
                    }
                }

                Debug.WriteLine($"✓ Geo files ready in: {geoDir}");
                return geoDir;
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"ERROR extracting geo files: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return string.Empty;
            }
        }

        private void UpdateNotification(string title, string text)
        {
            try
            {
                var notification = CreateNotification(title, text);
                var nid = NOTIFICATION_ID;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        var nm = GetSystemService(NotificationService) as NotificationManager;
                        nm?.Notify(nid, notification);
                    }
                    catch (SysException ex) { Debug.WriteLine($"UpdateNotification: {ex.Message}"); }
                });
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"Failed to update notification: {ex.Message}");
            }
        }

        public override void OnDestroy()
        {
            Debug.WriteLine("=== VPN Service Stopping ===");

            _isRunning = false;

            // Закрываем VPN интерфейс
            try
            {
                _vpnInterface?.Close();
                _vpnInterface = null;
                Debug.WriteLine("VPN interface closed");
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"Error closing VPN interface: {ex.Message}");
            }

            // Останавливаем Xray
            try
            {
                if (_xrayProcess != null && !_xrayProcess.HasExited)
                {
                    _xrayProcess.Kill();
                    _xrayProcess.WaitForExit(3000);
                    Debug.WriteLine("Xray process terminated");
                }
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"Error stopping Xray: {ex.Message}");
            }

            // Останавливаем hev-socks5-tunnel (in-process)
            if (_useHevTun2Socks)
            {
                try
                {
                    HevSocks5Tunnel.Stop();
                    Debug.WriteLine("hev-socks5-tunnel stopped");
                }
                catch (SysException ex) { Debug.WriteLine($"Error stopping hev: {ex.Message}"); }
            }

            // Останавливаем tun2socks (внешний процесс)
            try
            {
                if (tun2socksProcess != null && !tun2socksProcess.HasExited)
                {
                    tun2socksProcess.Kill();
                    tun2socksProcess.WaitForExit(3000);
                    Debug.WriteLine("tun2socksProcess terminated");
                }
            }
            catch (SysException ex)
            {
                Debug.WriteLine($"Error stopping tun2socks: {ex.Message}");
            }

            StopMonitoringThread();

            // Удаляем временный конфиг
            if (!string.IsNullOrEmpty(_tempConfigPath) && SysFile.Exists(_tempConfigPath))
            {
                try { SysFile.Delete(_tempConfigPath); }
                catch { }
            }

            base.OnDestroy();
            Debug.WriteLine("=== VPN Service Stopped ===");
        }

    }
}
