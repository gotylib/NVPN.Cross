using NVPN.Cross.BL.Dal.Models;


namespace NVPN.Cross.BL.Services.Interfaces
{
    internal interface IVpnConnectService
    {
        internal bool Connect(VlessProfile profile);

        internal bool Disconnect(VlessProfile profile);
    }
}
