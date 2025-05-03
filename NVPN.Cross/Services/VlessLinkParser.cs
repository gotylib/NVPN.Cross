using System;
using System.Web;

namespace NVPN.Native.Services
{
    public static class VlessLinkParser
    {
        public static VpnConnectionOptions Parse(string vlessLink)
        {
            var uri = new Uri(vlessLink);
            var query = HttpUtility.ParseQueryString(uri.Query);

            return new VpnConnectionOptions(
                Protocol: "vless",
                ServerAddress: uri.Host,
                ServerPort: uri.Port,
                Username: uri.UserInfo.Split(':')[0], // UUID
                Password: "",
                Flow: query["flow"] ?? "",
                Security: query["security"] ?? "tls",
                Sni: query["sni"] ?? "",
                Fingerprint: query["fp"] ?? "chrome",
                PublicKey: query["pbk"] ?? "",
                ShortId: query["sid"] ?? "",
                Spx: query["spx"] ?? "",
                Type: query["type"] ?? "tcp"
            );
        }
    }
}