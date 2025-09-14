using System.Web;

namespace NVPN.Cross.Dal.Models
{
    public class VlessProfile
    {
        public int Id { get; set; }
        public string Address { get; set; } = string.Empty;
        public int Port { get; set; }
        public string UserInfo { get; set; } = string.Empty;
        public string Security { get; set; } = string.Empty;
        public string Network { get; set; } = string.Empty;
        public string Flow { get; set; } = string.Empty;
        public string Sni { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string ShortId { get; set; } = string.Empty;
        public string Spx { get; set; } = string.Empty;
        public string Sid { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;

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
                UserInfo = user,
                Security = query["security"] ?? "none",
                Network = query["type"] ?? "tcp",
                Flow = query["flow"] ?? string.Empty,
                Sni = query["sni"] ?? string.Empty,
                PublicKey = query["pbk"] ?? string.Empty,
                Fingerprint = query["fp"] ?? string.Empty,
                ShortId = query["sid"] ?? string.Empty,
                Spx = query["spx"] ?? string.Empty,
                Sid = query["sid"] ?? string.Empty,
                Remarks = fragment ?? string.Empty
            };
        }

        internal static object GenerateXrayConfig(VlessProfile p, int inboundPort)
        {
            var tempDir = Path.GetTempPath();
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
                    access = Path.Combine(tempDir, "xray_access.log"),
                    error = Path.Combine(tempDir, "xray_error.log"),
                    loglevel = "debug"
                },
                dns = new
                {
                    servers = new object[]
                    {
                        "8.8.8.8",
                        "1.1.1.1",
                        "localhost"
                    }
                },
                inbounds = new object[]
                {
                    new
                    {
                        tag = "socks-in",
                        protocol = "socks",
                        listen = "127.0.0.1",
                        port = inboundPort,
                        settings = new
                        {
                            udp = true,
                            auth = "noauth"
                        }
                    },
                    new
                    {
                        tag = "http-in",
                        protocol = "http",
                        listen = "127.0.0.1",
                        port = inboundPort + 1,
                        settings = new { }
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
                                            id = p.UserInfo,
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
                            realitySettings,
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
                            inboundTag = new string[] { "socks-in", "http-in" },
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
                        },
                        new
                        {
                            type = "field",
                            ip = new string[] { "0.0.0.0/0" },
                            outboundTag = "proxy"
                        }
                    }
                }
            };
        }
    }
}
