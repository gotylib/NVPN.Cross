using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using NVPN.Cross.BL.Services.Interfaces;
using NVPN.Cross.Dal.Models;
using AndroidVpnServiceBase = NVPN.Cross.Platforms.Android.AndroidServices.AndroidVpnServiceBase;
using System.Diagnostics;
using Debug = System.Diagnostics.Debug;

namespace NVPN.Cross.Platforms.Android.Services
{
    internal class AndroidVpnConnectService : IVpnConnectService
    {
        private static bool _isConnected = false;

        bool IVpnConnectService.Connect(VlessProfile profile, out string errorMsg)
        {
            try
            {
                errorMsg = string.Empty;
                
                Debug.WriteLine("AndroidVpnConnectService: Connect called");
                
                if (_isConnected)
                {
                    errorMsg = "VPN уже подключен";
                    return false;
                }

                if (Platform.CurrentActivity == null)
                {
                    errorMsg = "Current activity is null";
                    Debug.WriteLine("ERROR: Platform.CurrentActivity is null");
                    return false;
                }

                // Проверяем VPN разрешения
                var vpnIntent = VpnService.Prepare(Platform.CurrentActivity);
                if (vpnIntent != null)
                {
                    Debug.WriteLine("VPN permission not granted, requesting...");
                    
                    // Запускаем Activity для получения разрешения
                    Task.Run(async () =>
                    {
                        try
                        {
                            var granted = await VpnPermissionActivity.RequestVpnPermission(Platform.CurrentActivity, profile);
                            if (granted)
                            {
                                _isConnected = true;
                                Debug.WriteLine("VPN permission granted and service started");
                            }
                            else
                            {
                                Debug.WriteLine("VPN permission denied");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error in permission request: {ex.Message}");
                        }
                    });
                    
                    errorMsg = "Запрос разрешения VPN...";
                    return false;
                }

                Debug.WriteLine("VPN permission already granted, starting service");

                var socksTunMode = Microsoft.Maui.Storage.Preferences.Default.Get("UseSocksTunMode", false)
                    || AndroidVpnServiceBase.UseSocksTunModeOverride;
                Debug.WriteLine($"[Connect] UseSocksTunMode: {socksTunMode} (Preferences + Override)");

                // Разрешение уже есть, сразу запускаем сервис
                var intent = new Intent(Platform.CurrentActivity, typeof(AndroidServices.AndroidVpnServiceBase));
                intent.PutExtra("profile", System.Text.Json.JsonSerializer.Serialize(profile));
                intent.PutExtra("socksTunMode", socksTunMode);
                
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    Platform.CurrentActivity.StartForegroundService(intent);
                }
                else
                {
                    Platform.CurrentActivity.StartService(intent);
                }
                
                _isConnected = true;
                Debug.WriteLine("VPN service started successfully");
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = $"Ошибка подключения: {ex.Message}";
                Debug.WriteLine($"ERROR in Connect: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        bool IVpnConnectService.Disconnect(VlessProfile profile, out string errorMsg)
        {
            try
            {
                errorMsg = string.Empty;
                
                Debug.WriteLine("AndroidVpnConnectService: Disconnect called");
                
                if (!_isConnected)
                {
                    errorMsg = "VPN не подключен";
                    return false;
                }

                if (Platform.CurrentActivity == null)
                {
                    errorMsg = "Current activity is null";
                    return false;
                }

                // Останавливаем сервис
                var intent = new Intent(Platform.CurrentActivity, typeof(AndroidServices.AndroidVpnServiceBase));
                Platform.CurrentActivity.StopService(intent);
                
                _isConnected = false;
                Debug.WriteLine("VPN service stopped successfully");
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = $"Ошибка отключения: {ex.Message}";
                Debug.WriteLine($"ERROR in Disconnect: {ex.Message}");
                return false;
            }
        }
    }
}
