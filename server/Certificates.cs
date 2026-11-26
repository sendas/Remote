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
        {
            var old = new X509Certificate2(path, (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
            // os browsers (sobretudo iPhone/Mac) recusam certificados com mais de 398 dias de validade:
            // renova com a MESMA chave (o pin da chave pública dos agentes não muda)
            var tooLong = (old.NotAfter - old.NotBefore).TotalDays > 397;
            var expiring = old.NotAfter < DateTime.Now.AddDays(45);
            if (!tooLong && !expiring) return old;
            try
            {
                using var oldKey = old.GetRSAPrivateKey();
                if (oldKey == null) return old;
                using var key = RSA.Create();
                // exportar cifrado funciona mesmo quando o Windows não deixa exportar a chave "em claro"
                var tmpPw = Guid.NewGuid().ToString("N");
                var blob = oldKey.ExportEncryptedPkcs8PrivateKey(tmpPw,
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1));
                key.ImportEncryptedPkcs8PrivateKey(tmpPw, blob, out _);
                var renewed = CreateSelfSigned(key);
                try { File.Copy(path, path + ".antigo", true); } catch { }
                File.WriteAllBytes(path, renewed.Export(X509ContentType.Pfx));
                old.Dispose();
                return new X509Certificate2(path, (string?)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
            }
            catch { return old; }
        }

        using var newKey = RSA.Create(2048);
        var cert = CreateSelfSigned(newKey);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return new X509Certificate2(path, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
    }

    /// <summary>Bytes do PFX em uso e respetiva palavra-passe (para o motor TLS gerido).</summary>
    public static (byte[] Pfx, string? Password) PfxBytes(ServerConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.CertificatePath)
            ? (File.ReadAllBytes(cfg.CertificatePath), cfg.CertificatePassword)
            : (File.ReadAllBytes(Path.Combine(ServerConfig.DataDir, "server.pfx")), null);

    /// <summary>SHA-256 da chave pública (hex). É este valor que se põe em "CertificatePin" no agente:
    /// não muda quando o certificado é renovado.</summary>
    public static string Pin(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    private static X509Certificate2 CreateSelfSigned(RSA key)
    {
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

        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(396));
    }
}
