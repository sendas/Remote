using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClassroomServer;

public static class Certificates
{
    /// <summary>
    /// Devolve o certificado configurado ou, se não houver, um autoassinado persistente em data/server.pfx.
    /// </summary>
    public static X509Certificate2 Load(ServerConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.CertificatePath))
            return new X509Certificate2(cfg.CertificatePath, cfg.CertificatePassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);

        var path = Path.Combine(ServerConfig.DataDir, "server.pfx");
        if (File.Exists(path))
            return new X509Certificate2(path, (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);

        var cert = CreateSelfSigned();
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return new X509Certificate2(path, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
    }

    /// <summary>SHA-256 do certificado (hex). É este valor que se põe em "CertificatePin" no agente.</summary>
    public static string Pin(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData));

    private static X509Certificate2 CreateSelfSigned()
    {
        using var key = RSA.Create(2048);
        var host = Dns.GetHostName();
        var req = new CertificateRequest($"CN={host}, O=Sala de Aula", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName("localhost");
        try
        {
            var fqdn = Dns.GetHostEntry(host).HostName;
            if (!string.Equals(fqdn, host, StringComparison.OrdinalIgnoreCase)) san.AddDnsName(fqdn);
        }
        catch { /* sem DNS */ }
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                san.AddIpAddress(ua.Address);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }
}
