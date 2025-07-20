using NVPN.Cross.BL.Services.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Net.NetworkInformation;
using System.Linq;
using NVPN.Cross.Dal.Models;

namespace NVPN.Cross.Platforms.Windows.WindowsServices
{
    internal class WindowsVpnConnectService : IVpnConnectService
    {
        private static Process xrayProcess;
        private static string tempConfigPath;

        bool IVpnConnectService.Connect(VlessProfile profile, out string errorMsg)
        {
            errorMsg = string.Empty;
            try
            {
                // 0. Найти свободный порт, начиная с 10809
                int startPort = 10809;
                int maxPort = 10909;
                int selectedPort = -1;
                var ipProps = IPGlobalProperties.GetIPGlobalProperties();
                var usedPorts = ipProps.GetActiveTcpListeners()
                    .Where(ep => ep.Port >= startPort && ep.Port <= maxPort)
                    .Select(ep => ep.Port)
                    .ToHashSet();

                for (int port = startPort; port <= maxPort; port++)
                {
                    if (!usedPorts.Contains(port))
                    {
                        selectedPort = port;
                        break;
                    }
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
                tempConfigPath = Path.Combine(Path.GetTempPath(), $"xray_{Guid.NewGuid()}.json");
                File.WriteAllText(tempConfigPath, json);

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
                    Arguments = $"-c {tempConfigPath}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                try
                {
                    xrayProcess = Process.Start(psi);
                    if (xrayProcess == null || xrayProcess.HasExited)
                    {
                        string stdErr = xrayProcess?.StandardError.ReadToEnd() ?? "process not started";
                        string stdOut = xrayProcess?.StandardOutput.ReadToEnd() ?? "";
                        errorMsg = $"Не удалось запустить xray.exe\nSTDERR: {stdErr}\nSTDOUT: {stdOut}";
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
                var proc1 = Process.Start(setProxy); proc1.WaitForExit();
                if (proc1.ExitCode != 0)
                {
                    errorMsg = "Ошибка установки ProxyEnable через reg";
                    return false;
                }
                var setProxyServer = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = $"add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyServer /t REG_SZ /d 127.0.0.1:{selectedPort} /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc2 = Process.Start(setProxyServer); proc2.WaitForExit();
                if (proc2.ExitCode != 0)
                {
                    errorMsg = "Ошибка установки ProxyServer через reg";
                    return false;
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
                if (xrayProcess != null && !xrayProcess.HasExited)
                {
                    xrayProcess.Kill();
                    xrayProcess.Dispose();
                    xrayProcess = null;
                }
                // 2. Удаляем временный конфиг
                if (!string.IsNullOrEmpty(tempConfigPath) && File.Exists(tempConfigPath))
                {
                    File.Delete(tempConfigPath);
                    tempConfigPath = null;
                }
                // 3. Сбрасываем системный прокси
                var disableProxy = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(disableProxy); proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    errorMsg = "Ошибка сброса ProxyEnable через reg";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.ToString();
                return false;
            }
        }
    }
}