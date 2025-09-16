using NVPN.Cross.BL.Services.Interfaces;
using System.Diagnostics;
using System.Text.Json;
using System.Net.NetworkInformation;
using NVPN.Cross.Dal.Models;

namespace NVPN.Cross.Platforms.Windows.WindowsServices
{
    internal class WindowsVpnConnectService : IVpnConnectService
    {
        private static Process? _xrayProcess;
        private static string? _tempConfigPath;

        bool IVpnConnectService.Connect(VlessProfile profile, out string errorMsg)
        {
            errorMsg = string.Empty;
            try
            {
                // 0. Найти свободный порт, начиная с 10809
                const int startPort = 10809;
                const int maxPort = 10909;
                var selectedPort = -1;
                var ipProps = IPGlobalProperties.GetIPGlobalProperties();
                var usedPorts = ipProps.GetActiveTcpListeners()
                    .Where(ep => ep.Port is >= startPort and <= maxPort)
                    .Select(ep => ep.Port)
                    .ToHashSet();

                for (var port = startPort; port <= maxPort; port++)
                {
                    if (usedPorts.Contains(port)) continue;
                    
                    selectedPort = port;
                    break;
                }
                // Если все порты заняты, вернуть ошибку
                if (selectedPort == -1)
                {
                    errorMsg = $"No available ports in range {startPort}-{maxPort}";
                    return false;
                }

                // 1. Генерируем конфиг
                var config = VlessProfile.GenerateXrayConfig(profile, selectedPort);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                _tempConfigPath = Path.Combine(Path.GetTempPath(), $"xray_{Guid.NewGuid()}.json");
                File.WriteAllText(_tempConfigPath, json);

                // 2. Запускаем xray.exe
                var xrayExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Xray", "Windows", "xray.exe");
                if (!File.Exists(xrayExePath))
                {
                    errorMsg = $"Не найден xray.exe по пути: {xrayExePath}";
                    return false;
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
                    _xrayProcess = Process.Start(psi) ?? throw new InvalidOperationException();
                    if (_xrayProcess.HasExited)
                    {
                        // Read any error output for diagnostics
                        var stdErr = _xrayProcess.StandardError.ReadToEnd();
                        var stdOut = _xrayProcess?.StandardOutput.ReadToEnd() ?? "";
                        errorMsg = $"Не удалось запустить xray.exe\nSTDERR: {stdErr}\nSTDOUT: {stdOut}";
                        return false;
                    }
                    // Optional: small delay to allow background startup
                    Thread.Sleep(200);
                    // If xray.exe exited immediately, capture error output
                    if (_xrayProcess.HasExited)
                    {
                        var stdErrNow = _xrayProcess.StandardError.ReadToEnd();
                        var stdOutNow = _xrayProcess.StandardOutput.ReadToEnd();
                        errorMsg = $"xray.exe сразу завершился. Попробуйте запустить вручную:\nSTDERR: {stdErrNow}\nSTDOUT: {stdOutNow}";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    errorMsg = $"Ошибка запуска xray.exe: {ex.Message}\n{ex}";
                    return false;
                }

                // 3. Устанавливаем системный прокси (для текущего пользователя)
                var setProxy = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 1 /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc1 = Process.Start(setProxy); 
                proc1?.WaitForExit();
                
                if (proc1 != null && proc1.ExitCode != 0)
                {
                    errorMsg = "Ошибка установки ProxyEnable через reg";
                    return false;
                }
                // Устанавливаем HTTP прокси для системных приложений (входной HTTP порт = selectedPort + 1)
                int httpPort = selectedPort + 1;
                var setProxyServer = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = $"add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyServer /t REG_SZ /d 127.0.0.1:{httpPort} /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc2 = Process.Start(setProxyServer); 
                proc2?.WaitForExit();
                
                if (proc2 != null && proc2.ExitCode != 0)
                {
                    errorMsg = "Ошибка установки ProxyServer через reg";
                    return false;
                }

                // 4. Try to synchronize WinHTTP proxy from IE settings (may require admin privileges)
                try
                {
                    var syncWinHttp = new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = "winhttp import proxy source=ie",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var proc3 = Process.Start(syncWinHttp);
                    proc3?.WaitForExit();
                    if (proc3 != null && proc3.ExitCode != 0)
                    {
                        // Warn user but do not abort connection
                        errorMsg = "Предупреждение: не удалось синхронизировать WinHTTP прокси (ExitCode=" + proc3.ExitCode + "). " +
                                   "Возможно, требуются права администратора.";
                    }
                }
                catch (Exception ex)
                {
                    // Warn user but continue
                    errorMsg = "Предупреждение: ошибка синхронизации WinHTTP прокси: " + ex.Message;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.ToString();
                return false;
            }
        }

        bool IVpnConnectService.Disconnect(VlessProfile profile, out string errorMsg)
        {
            errorMsg = string.Empty;
            try
            {
                // 1. Останавливаем xray
                if (_xrayProcess is { HasExited: false })
                {
                    _xrayProcess.Kill();
                    _xrayProcess.Dispose();
                    _xrayProcess = null;
                }
                // 2. Удаляем временный конфиг
                if (!string.IsNullOrEmpty(_tempConfigPath) && File.Exists(_tempConfigPath))
                {
                    File.Delete(_tempConfigPath);
                    _tempConfigPath = null;
                }
                // 3. Сбрасываем системный прокси
                var disableProxy = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(disableProxy);
                proc?.WaitForExit();
                if (proc is { ExitCode: 0 }) return true;
                
                errorMsg = "Ошибка сброса ProxyEnable через reg";
                return false;
            }
            catch (Exception ex)
            {
                errorMsg = ex.ToString();
                return false;
            }
        }
    }
}