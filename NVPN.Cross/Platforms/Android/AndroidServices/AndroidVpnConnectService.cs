using Android.App;
using Android.Content;
using Android.OS;
using NVPN.Cross.BL.Services.Interfaces;
using NVPN.Cross.Dal.Models;
using NVPN.Cross.Platforms.Android.AndroidServices;

namespace NVPN.Cross.Platforms.Android.Services
{
    internal class AndroidVpnConnectService : IVpnConnectService
    {
        private static AndroidVpnServiceBase? _currentService;
        private static bool _isConnected = false;

        bool IVpnConnectService.Connect(VlessProfile profile, out string errorMsg)
        {
            try
            {
                errorMsg = string.Empty;
                
                if (_isConnected)
                {
                    errorMsg = "VPN уже подключен";
                    return false;
                }

                // Создаем Intent для запуска VPN сервиса
                var intent = new Intent(Platform.CurrentActivity, typeof(AndroidVpnServiceBase));
                intent.PutExtra("profile", System.Text.Json.JsonSerializer.Serialize(profile));
                
                // Запускаем foreground сервис с типом VPN
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    Platform.CurrentActivity?.StartForegroundService(intent);
                }
                else
                {
                    Platform.CurrentActivity?.StartService(intent);
                }
                
                _isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }

        bool IVpnConnectService.Disconnect(VlessProfile profile, out string errorMsg)
        {
            try
            {
                errorMsg = string.Empty;
                
                if (!_isConnected)
                {
                    errorMsg = "VPN не подключен";
                    return false;
                }

                // Останавливаем сервис
                var intent = new Intent(Platform.CurrentActivity, typeof(AndroidVpnServiceBase));
                Platform.CurrentActivity?.StopService(intent);
                
                _isConnected = false;
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }
    }
}
