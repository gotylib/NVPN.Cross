using NVPN.Cross.BL.Services.Interfaces;
using NVPN.Cross.Dal.Models;

namespace NVPN.Cross.Platforms.Android.Services
{
    internal class AndroidVpnConnectService : IVpnConnectService 
    {
        bool IVpnConnectService.Connect(VlessProfile profile, out string errorMsg)
        {
            throw new NotImplementedException();
        }

        bool IVpnConnectService.Disconnect(VlessProfile profile, out string errorMsg)
        {
            throw new NotImplementedException();
        }
    }
}
