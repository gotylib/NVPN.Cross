using NVPN.Cross.Dal.Models;


namespace NVPN.Cross.BL.Services.Interfaces
{
    internal interface IVpnConnectService
    {
        internal bool Connect(VlessProfile profile, out string errorMsg);

        internal bool Disconnect(VlessProfile profile, out string errorMsg);
    }
}
