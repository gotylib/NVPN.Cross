using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace NVPN.Cross.BL.Dal.Models
{
    internal class VlessProfile
    {
        public string Address { get; set; }
        public int Port { get; set; }
        public string Id { get; set; }
        public string Security { get; set; }
        public string Network { get; set; }
        public string Flow { get; set; }
        public string Sni { get; set; }
        public string PublicKey { get; set; }
        public string Fingerprint { get; set; }
        public string ShortId { get; set; }
        public string Spx { get; set; }
        public string Sid { get; set; }
        public string Remarks { get; set; }

        internal static VlessProfile ParseVlessUrl(string url)
        {
            // vless://uuid@host:port?...#remarks
            var uri = new Uri(url.Replace("vless://", "http://")); // hack for Uri parser
            var user = uri.UserInfo;
            var host = uri.Host;
            var port = uri.Port;
            var query = HttpUtility.ParseQueryString(uri.Query);
            var fragment = uri.Fragment.StartsWith("#") ? uri.Fragment.Substring(1) : uri.Fragment;

            return new VlessProfile
            {
                Address = host,
                Port = port,
                Id = user,
                Security = query["security"] ?? "none",
                Network = query["type"] ?? "tcp",
                Flow = query["flow"] ?? string.Empty,
                Sni = query["sni"] ?? string.Empty,
                PublicKey = query["pbk"] ?? string.Empty,
                Fingerprint = query["fp"] ?? string.Empty,
                ShortId = query["sid"] ?? string.Empty,
                Spx = query["spx"] ?? string.Empty,
                Remarks = fragment
            };
        }

        internal static object GenerateXrayConfig(VlessProfile p)
        {
            var realitySettings = p.Security == "reality"
                ? new
                {
                    show = false,
                    serverName = p.Sni,
                    publicKey = p.PublicKey,
                    shortId = p.ShortId,
                    spiderX = p.Spx,
                    fingerprint = p.Fingerprint
                }
                : null;

            return new
            {
                log = new
                {
                    access = "access.log",
                    error = "error.log",
                    loglevel = "warning"
                },
                inbounds = new object[]
                {
                    new
                    {
                        tag = "tun-in",
                        protocol = "tun",
                        listen = "127.0.0.1",
                        port = 3001,
                        settings = new
                        {
                            name = "xray-tun",
                            address = new string[] { "10.0.0.2/30", "fdfe:dcba:9876::2/126" },
                            gateway = new string[] { "10.0.0.1", "fdfe:dcba:9876::1" },
                            mtu = 1500,
                            stack = "system"
                        }
                    }
                },
                outbounds = new object[]
                {
                    new
                    {
                        tag = "proxy",
                        protocol = "vless",
                        settings = new
                        {
                            vnext = new object[]
                            {
                                new
                                {
                                    address = p.Address,
                                    port = p.Port,
                                    users = new object[]
                                    {
                                        new
                                        {
                                            id = p.Id,
                                            encryption = "none",
                                            flow = string.IsNullOrEmpty(p.Flow) ? null : p.Flow
                                        }
                                    }
                                }
                            }
                        },
                        streamSettings = new
                        {
                            network = p.Network,
                            security = p.Security,
                            realitySettings = realitySettings,
                            tlsSettings = p.Security == "tls" ? new
                            {
                                serverName = p.Sni,
                                allowInsecure = false
                            } : null
                        }
                    },
                    new
                    {
                        protocol = "freedom",
                        tag = "direct"
                    },
                    new
                    {
                        protocol = "blackhole",
                        tag = "block"
                    }
                },
                routing = new
                {
                    domainStrategy = "IPIfNonMatch",
                    rules = new object[]
                    {
                        new
                        {
                            type = "field",
                            inboundTag = new string[] { "tun-in" },
                            outboundTag = "proxy"
                        },
                        new
                        {
                            type = "field",
                            ip = new string[] { "geoip:private" },
                            outboundTag = "direct"
                        },
                        new
                        {
                            type = "field",
                            domain = new string[] { "geosite:category-ads-all" },
                            outboundTag = "block"
                        }
                    }
                }
            };
        }
    }
}
