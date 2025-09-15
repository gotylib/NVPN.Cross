using Android.Net;
using NVPN.Cross.BL.Services.Interfaces;
using NVPN.Cross.Dal.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
