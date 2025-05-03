namespace NVPN.Native.Services
{
    public record VpnConnectionOptions(
        string Protocol,      // "vless", "vmess" и т.д.
        string ServerAddress,
        int ServerPort,
        string Username,     // Для VLESS это UUID
        string Password,
        string Flow = "",
        string Security = "tls",
        string Sni = "",
        string Fingerprint = "chrome",
        string PublicKey = "",
        string ShortId = "",
        string Spx = "",
        string Type = "tcp"  // "tcp", "ws" и т.д.
    );
}