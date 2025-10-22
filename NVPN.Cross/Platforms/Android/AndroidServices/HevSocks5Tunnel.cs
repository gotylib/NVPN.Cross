using System.Runtime.InteropServices;
using System.Text;

namespace NVPN.Cross.Platforms.Android.AndroidServices;

/// <summary>
/// P/Invoke для libhev-socks5-tunnel.so.
/// Библиотека принимает TUN FD in-process — обходит ограничения SELinux.
/// </summary>
internal static class HevSocks5Tunnel
{
    private static bool? _isAvailable;

    /// <summary>true если libhev-socks5-tunnel.so загружена.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_isAvailable.HasValue) return _isAvailable.Value;
            try
            {
                NativeLibrary.Load("hev-socks5-tunnel");
                _isAvailable = true;
            }
            catch
            {
                _isAvailable = false;
            }
            return _isAvailable.Value;
        }
    }
    private const string LibName = "hev-socks5-tunnel";

    [DllImport(LibName, EntryPoint = "hev_socks5_tunnel_main_from_str", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TunnelMainFromStr(
        byte[] configStr,
        uint configLen,
        int tunFd);

    [DllImport(LibName, EntryPoint = "hev_socks5_tunnel_quit", CallingConvention = CallingConvention.Cdecl)]
    private static extern void TunnelQuit();

    /// <summary>
    /// Запускает tun2socks в фоне. Блокирует до вызова Stop() или ошибки.
    /// </summary>
    /// <param name="tunFd">TUN file descriptor (от ParcelFileDescriptor.DetachFd)</param>
    /// <param name="socksHost">SOCKS5 хост, например 127.0.0.1</param>
    /// <param name="socksPort">SOCKS5 порт, например 10809</param>
    /// <returns>0 при успехе, -1 при ошибке</returns>
    public static int Run(int tunFd, string socksHost = "127.0.0.1", int socksPort = 10809)
    {
        var yaml = BuildYamlConfig(socksHost, socksPort);
        var bytes = Encoding.UTF8.GetBytes(yaml);
        return TunnelMainFromStr(bytes, (uint)bytes.Length, tunFd);
    }

    /// <summary>Останавливает туннель. Вызвать из другого потока.</summary>
    public static void Stop()
    {
        try { TunnelQuit(); }
        catch { /* ignore */ }
    }

    private static string BuildYamlConfig(string socksHost, int socksPort)
    {
        return $@"tunnel:
  name: tun0
  mtu: 1500
  ipv4: 10.0.0.2

socks5:
  address: {socksHost}
  port: {socksPort}
  udp: 'udp'
";
    }
}
