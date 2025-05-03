namespace NVPN.Native.Services.Interfaces
{
    public interface IVpnService
    {
        Task<bool> ConnectAsync(VpnConnectionOptions options);
        Task DisconnectAsync();
        Task<VpnStatus> GetStatusAsync();
        event EventHandler<VpnStatus> StatusChanged;
    }

    public enum VpnStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting,
        Error
    }
}