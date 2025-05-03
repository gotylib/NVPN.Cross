using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Java.IO;
using Java.Net;

namespace NVPN.Native.Platforms.Android.Services
{
    [Service(Exported = true, Permission = "android.permission.BIND_VPN_SERVICE")]
    public class CustomVpnService : VpnService
    {
        private ParcelFileDescriptor _vpnInterface;

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == "disconnect")
            {
                Disconnect();
                return StartCommandResult.NotSticky;
            }

            var builder = new Builder(this)
                .SetSession("NVPN")
                .AddAddress("10.0.0.2", 24)
                .AddRoute("0.0.0.0", 0);

            _vpnInterface = builder.Establish();
            new System.Threading.Thread(ProcessTraffic).Start();
            return StartCommandResult.Sticky;
        }

        private void ProcessTraffic()
        {
            try
            {
                var socket = new Java.Net.Socket("127.0.0.1", 10808);

                using var input = new FileInputStream(_vpnInterface.FileDescriptor);
                using var output = new FileOutputStream(_vpnInterface.FileDescriptor);

                var buffer = new byte[32767];
                while (true)
                {
                    var length = input.Read(buffer);
                    socket.OutputStream.Write(buffer, 0, length);

                    length = socket.InputStream.Read(buffer);
                    output.Write(buffer, 0, length);
                }
            }
            catch { Disconnect(); }
        }

        public void Disconnect()
        {
            _vpnInterface?.Close();
            StopSelf();
        }
    }
}