using NVPN.Cross.BL.Dal.Models;
using NVPN.Cross.BL.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NVPN.Cross.Platforms.Windows.Services
{
    internal class VpnConnectService : IVpnConnectService
    {
        bool IVpnConnectService.Connect(VlessProfile profile)
        {
            throw new NotImplementedException();
        }

        bool IVpnConnectService.Disconnect(VlessProfile profile)
        {
            throw new NotImplementedException();
        }
    }
}
