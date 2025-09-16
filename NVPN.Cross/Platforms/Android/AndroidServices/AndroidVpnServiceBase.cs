using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using System.Diagnostics;
using Process = System.Diagnostics.Process;

namespace NVPN.Cross.Platforms.Android.AndroidServices
{
    [Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
    [IntentFilter([ServiceInterface])]
    public class AndroidVpnServiceBasen : VpnService
    {
        private ParcelFileDescriptor? vpnInterface;
        private Process tun2socksProcess;
        private Process xrayProcess;
        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            // 1. Создаём VPN-интерфейс через Builder
            var builder = new Builder(this);
            builder.SetSession("NVPN")
                   .AddAddress("10.0.0.2", 32)
                   .AddRoute("0.0.0.0", 0)
                   .AddDnsServer("8.8.8.8")
                   .SetMtu(1500);

            var vpnInterface = builder.Establish();

            // 2. Запускаем xray и tun2socks (как процессы)
            var filesDir = ApplicationContext.FilesDir.AbsolutePath;
            var xrayPath = Path.Combine(filesDir, "xray");
            var configPath = Path.Combine(filesDir, "xray_config.json");
            StartProcess(ref xrayProcess, xrayPath, $"-c {configPath}");

            var tun2socksPath = Path.Combine(filesDir, "tun2socks");
            var tunFd = vpnInterface?.Fd ?? -1;
            const int socksPort = 10809;
            StartProcess(ref tun2socksProcess, tun2socksPath, $"--netif-ipaddr 10.0.0.2 --netif-netmask 255.255.255.0 --socks-server-addr 127.0.0.1:{socksPort} --tunfd {tunFd} --loglevel info");

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            vpnInterface?.Close();
            vpnInterface = null;
            try { tun2socksProcess.Kill(); }
            catch
            {
                // ignored
            }

            try { xrayProcess.Kill(); }
            catch
            {
                // ignored
            }

            base.OnDestroy();
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
}
