using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomAgent;

/// <summary>
/// Pipes do Windows através da rede (SMB) não aceitam escritas maiores que ~64 KB:
/// a escrita falha. Este invólucro parte cada escrita em pedaços de 32 KB.
/// (Sem isto, as miniaturas passavam mas o ecrã em grande — imagens maiores — nunca chegava.)
/// </summary>
internal sealed class ChunkedStream : Stream
{
    private const int Max = 32 * 1024;
    private readonly Stream _s;
    public ChunkedStream(Stream s) => _s = s;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();

    public override int Read(byte[] b, int o, int c) => _s.Read(b, o, c);
    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => _s.ReadAsync(b, o, c, ct);

    public override void Write(byte[] b, int o, int c)
    {
        while (c > 0) { var n = Math.Min(c, Max); _s.Write(b, o, n); o += n; c -= n; }
    }

    public override async Task WriteAsync(byte[] b, int o, int c, CancellationToken ct)
    {
        while (c > 0) { var n = Math.Min(c, Max); await _s.WriteAsync(b, o, n, ct).ConfigureAwait(false); o += n; c -= n; }
    }

#if NET
    public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => _s.ReadAsync(b, ct);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
    {
        while (b.Length > 0) { var n = Math.Min(b.Length, Max); await _s.WriteAsync(b.Slice(0, n), ct).ConfigureAwait(false); b = b.Slice(n); }
    }
#endif

    public override void Flush() => _s.Flush();
    public override Task FlushAsync(CancellationToken ct) => _s.FlushAsync(ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _s.Dispose();
        base.Dispose(disposing);
    }
}
