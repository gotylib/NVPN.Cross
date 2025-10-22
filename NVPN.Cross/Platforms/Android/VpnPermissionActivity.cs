using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using System.Diagnostics;
using NVPN.Cross.Dal.Models;
using Activity = Android.App.Activity;
using Debug = System.Diagnostics.Debug;

namespace NVPN.Cross.Platforms.Android
{
    [Activity(Label = "VPN Permission", Theme = "@style/Maui.SplashTheme")]
    public class VpnPermissionActivity : Activity
    {
        private const int VPN_REQUEST_CODE = 100;
        private static VlessProfile? _pendingProfile;
        private static TaskCompletionSource<bool>? _permissionTcs;

        public static Task<bool> RequestVpnPermission(Activity currentActivity, VlessProfile profile)
        {
            _pendingProfile = profile;
            _permissionTcs = new TaskCompletionSource<bool>();

            var intent = new Intent(currentActivity, typeof(VpnPermissionActivity));
            currentActivity.StartActivity(intent);

            return _permissionTcs.Task;
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            Debug.WriteLine("VpnPermissionActivity: OnCreate");

            // Проверяем разрешение VPN
            var vpnIntent = VpnService.Prepare(this);
            if (vpnIntent != null)
            {
                Debug.WriteLine("VpnPermissionActivity: Requesting VPN permission");
                StartActivityForResult(vpnIntent, VPN_REQUEST_CODE);
            }
            else
            {
                Debug.WriteLine("VpnPermissionActivity: VPN permission already granted");
                OnActivityResult(VPN_REQUEST_CODE, Result.Ok, null);
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            Debug.WriteLine($"VpnPermissionActivity: OnActivityResult - Code: {requestCode}, Result: {resultCode}");

            if (requestCode == VPN_REQUEST_CODE)
            {
                if (resultCode == Result.Ok)
                {
                    Debug.WriteLine("VPN permission granted, starting VPN service");
                    
                    // Разрешение получено, запускаем VPN сервис
                    if (_pendingProfile != null)
                    {
                        try
                        {
                            var intent = new Intent(this, typeof(AndroidServices.AndroidVpnServiceBase));
                            intent.PutExtra("profile", System.Text.Json.JsonSerializer.Serialize(_pendingProfile));
                            
                            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                            {
                                StartForegroundService(intent);
                            }
                            else
                            {
                                StartService(intent);
                            }
                            
                            _permissionTcs?.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error starting VPN service: {ex.Message}");
                            _permissionTcs?.TrySetResult(false);
                        }
                    }
                    else
                    {
                        _permissionTcs?.TrySetResult(false);
                    }
                }
                else
                {
                    Debug.WriteLine("VPN permission denied");
                    _permissionTcs?.TrySetResult(false);
                }

                Finish();
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Debug.WriteLine("VpnPermissionActivity: OnDestroy");
        }
    }
}
