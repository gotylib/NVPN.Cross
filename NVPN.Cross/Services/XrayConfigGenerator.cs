using NVPN.Native.Services;

namespace NVPN.Native.Services
{
    public static class XrayConfigGenerator
    {
        public static string Generate(VpnConnectionOptions options)
        {
            return $@"{{
                ""inbounds"": [{{
                    ""port"": 10808,
                    ""protocol"": ""socks"",
                    ""settings"": {{ 
                        ""auth"": ""noauth"",
                        ""udp"": true 
                    }}
                }}],
                ""outbounds"": [{{
                    ""protocol"": ""{options.Protocol}"",
                    ""settings"": {{
                        ""vnext"": [{{
                            ""address"": ""{options.ServerAddress}"",
                            ""port"": {options.ServerPort},
                            ""users"": [{{
                                ""id"": ""{options.Username}"",
                                ""flow"": ""{options.Flow}"",
                                ""encryption"": ""none""
                            }}]
                        }}]
                    }},
                    ""streamSettings"": {{
                        ""network"": ""{options.Type}"",
                        ""security"": ""{options.Security}"",
                        ""realitySettings"": {{
                            ""serverName"": ""{options.Sni}"",
                            ""publicKey"": ""{options.PublicKey}"",
                            ""shortId"": ""{options.ShortId}"",
                            ""spiderX"": ""{options.Spx}""
                        }},
                        ""tlsSettings"": {{
                            ""serverName"": ""{options.Sni}"",
                            ""fingerprint"": ""{options.Fingerprint}""
                        }}
                    }}
                }}]
            }}";
        }
    }
}