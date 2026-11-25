using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace ClassroomServer;

/// <summary>
/// HTTPS feito pelo próprio programa (BouncyCastle), sem usar o SChannel do Windows.
/// Necessário em Windows Server 2008 R2/2012, onde o SChannel não tem as versões/cifras
/// que os browsers atuais exigem (ERR_SSL_VERSION_OR_CIPHER_MISMATCH) e ativá-las obriga a reiniciar.
/// </summary>
public sealed class ManagedTls
{
    private readonly AsymmetricKeyParameter _key;
    private readonly byte[][] _chain;
    private readonly ILogger _log;

    public ManagedTls(byte[] pfx, string? password, ILogger log)
    {
        _log = log;
        var store = new Pkcs12StoreBuilder().Build();
        store.Load(new MemoryStream(pfx), (password ?? "").ToCharArray());
        var alias = store.Aliases.FirstOrDefault(store.IsKeyEntry)
                    ?? throw new InvalidOperationException("O certificado não tem chave privada.");
        _key = store.GetKey(alias).Key;
        _chain = store.GetCertificateChain(alias).Select(c => c.Certificate.GetEncoded()).ToArray();
    }

    /// <summary>Middleware de ligação do Kestrel: faz o handshake TLS e substitui o transporte pelo fluxo decifrado.</summary>
    public Func<ConnectionDelegate, ConnectionDelegate> Middleware => next => async ctx =>
    {
        var raw = new DuplexStream(ctx.Transport.Input.AsStream(), ctx.Transport.Output.AsStream());
        var protocol = new TlsServerProtocol(raw);
        try
        {
            // o BouncyCastle é síncrono: o handshake corre numa thread do pool.
            // Com prazo de 15 s: uma ligação que abre e não diz nada não pode prender threads para sempre.
            var accept = Task.Run(() => protocol.Accept(new Server(_key, _chain)));
            if (await Task.WhenAny(accept, Task.Delay(TimeSpan.FromSeconds(15))) != accept)
            {
                ctx.Abort(); // fecha o transporte: a leitura bloqueada no handshake termina com erro
                _ = accept.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return;
            }
            await accept;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Handshake TLS falhou de {Remote}: {Msg}", ctx.RemoteEndPoint, ex.Message);
            ctx.Abort();
            return;
        }

        // NB: o ReadAsync/WriteAsync de base do Stream partilham um semáforo (leitura pendente bloqueia escrita);
        // este adaptador corre cada operação síncrona na sua própria thread, para ler e escrever em simultâneo.
        var tls = new ConcurrentSyncStream(protocol.Stream);
        var original = ctx.Transport;
        ctx.Transport = new Pipe(PipeReader.Create(tls), PipeWriter.Create(tls));
        try
        {
            await next(ctx);
        }
        finally
        {
            ctx.Transport = original;
            try { protocol.Close(); } catch { }
        }
    };

    private sealed record Pipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

    private sealed class Server : DefaultTlsServer
    {
        private readonly AsymmetricKeyParameter _key;
        private readonly byte[][] _chain;

        public Server(AsymmetricKeyParameter key, byte[][] chain) : base(new BcTlsCrypto(new SecureRandom()))
        {
            _key = key;
            _chain = chain;
        }

        protected override ProtocolVersion[] GetSupportedVersions() =>
            ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

        // só suites com ECDHE (assinatura RSA) e TLS 1.3 — todas usam as credenciais de assinatura abaixo
        protected override int[] GetSupportedCipherSuites() => TlsUtilities.GetSupportedCipherSuites(Crypto, new[]
        {
            CipherSuite.TLS_AES_128_GCM_SHA256,
            CipherSuite.TLS_AES_256_GCM_SHA384,
            CipherSuite.TLS_CHACHA20_POLY1305_SHA256,
            CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
            CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
            CipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        });

        public override TlsCredentials GetCredentials() => GetRsaSignerCredentials();

        protected override TlsCredentialedSigner GetRsaSignerCredentials()
        {
            var v13 = TlsUtilities.IsTlsV13(m_context);
            var offered = m_context.SecurityParameters.ClientSigAlgs ?? new List<SignatureAndHashAlgorithm>();
            var prefs = v13
                ? new[] { SignatureAndHashAlgorithm.rsa_pss_rsae_sha256, SignatureAndHashAlgorithm.rsa_pss_rsae_sha384 }
                : new[]
                {
                    SignatureAndHashAlgorithm.rsa_pss_rsae_sha256,
                    new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.rsa),
                    new SignatureAndHashAlgorithm(HashAlgorithm.sha384, SignatureAlgorithm.rsa),
                    new SignatureAndHashAlgorithm(HashAlgorithm.sha1, SignatureAlgorithm.rsa),
                };
            var alg = prefs.FirstOrDefault(p => offered.Contains(p)) ?? prefs[0];

            var crypto = (BcTlsCrypto)Crypto;
            var certs = _chain.Select(c => (TlsCertificate)new BcTlsCertificate(crypto, c)).ToArray();
            var certificate = v13
                ? new Certificate(TlsUtilities.EmptyBytes, certs.Select(c => new CertificateEntry(c, null)).ToArray())
                : new Certificate(certs);
            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), crypto, _key, certificate, alg);
        }
    }

    private sealed class ConcurrentSyncStream : Stream
    {
        private readonly Stream _inner;
        private readonly object _writeGate = new();

        public ConcurrentSyncStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_writeGate) _inner.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            lock (_writeGate) _inner.Flush();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.Run(() => _inner.Read(buffer, offset, count), ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var arr = new byte[buffer.Length];
            return new ValueTask<int>(ReadAsync(arr, 0, arr.Length, ct).ContinueWith(t =>
            {
                var n = t.GetAwaiter().GetResult();
                arr.AsSpan(0, n).CopyTo(buffer.Span);
                return n;
            }, TaskScheduler.Default));
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.Run(() => Write(buffer, offset, count), ct);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var arr = buffer.ToArray();
            return new ValueTask(Task.Run(() => Write(arr, 0, arr.Length), ct));
        }

        public override Task FlushAsync(CancellationToken ct) => Task.Run(Flush, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Junta um stream de leitura e um de escrita num só stream bidirecional.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly Stream _in, _out;

        public DuplexStream(Stream input, Stream output)
        {
            _in = input;
            _out = output;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override void Flush() => _out.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
