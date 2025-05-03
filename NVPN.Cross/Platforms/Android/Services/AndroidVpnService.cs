using Android.App;
using Android.Content;
using NVPN.Native.Services;
using NVPN.Native.Services.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Application = Android.App.Application;
using Debug = System.Diagnostics.Debug;
using Process = System.Diagnostics.Process;

namespace NVPN.Native.Platforms.Android.Services
{
    public class AndroidVpnService : IVpnService
    {
        private Process _xrayProcess;
        private VpnStatus _currentStatus = VpnStatus.Disconnected;
        public event EventHandler<VpnStatus> StatusChanged;

        public async Task<bool> ConnectAsync(VpnConnectionOptions options)
        {
            try
            {
                UpdateStatus(VpnStatus.Connecting);

                await ExtractXrayBinary();
                var config = XrayConfigGenerator.Generate(options);
                File.WriteAllText(Path.Combine(Application.Context.FilesDir.Path, "config.json"), config);

                _xrayProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Application.Context.FilesDir.Path, "xray"),
                        Arguments = "run -config config.json",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                _xrayProcess.Start();

                var intent = new Intent(Application.Context, typeof(CustomVpnService));
                Application.Context.StartForegroundService(intent);

                UpdateStatus(VpnStatus.Connected);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting VPN: {ex}");
                UpdateStatus(VpnStatus.Error);
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            UpdateStatus(VpnStatus.Disconnecting);

            try
            {
                _xrayProcess?.Kill();
                var intent = new Intent(Application.Context, typeof(CustomVpnService));
                intent.SetAction("disconnect");
                Application.Context.StartService(intent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disconnecting VPN: {ex}");
            }

            UpdateStatus(VpnStatus.Disconnected);
        }

        public Task<VpnStatus> GetStatusAsync()
        {
            return Task.FromResult(_currentStatus);
        }

        private void UpdateStatus(VpnStatus newStatus)
        {
            _currentStatus = newStatus;
            StatusChanged?.Invoke(this, newStatus);
        }

        private async Task ExtractXrayBinary()
        {
            var dest = new Java.IO.File(Application.Context.FilesDir, "xray");
            if (dest.Exists()) return;

            using var asset = Application.Context.Assets.Open("xray");
            using var stream = new FileStream(dest.Path, FileMode.Create);
            await asset.CopyToAsync(stream);
            Java.Lang.Runtime.GetRuntime().Exec($"chmod 755 {dest.AbsolutePath}");
        }
    }
}