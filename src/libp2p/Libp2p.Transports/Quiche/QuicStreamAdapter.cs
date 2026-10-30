// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Quiche;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// A <see cref="Stream"/> over a quiche stream, so the transport can
/// hand quiche streams to <see cref="Core.StreamChannel"/> exactly as
/// the msquic transport hands it a QuicStream.
/// Semantics: reads return the next available bytes (partial reads
/// are normal, as for any Stream); an empty read means the peer
/// half-closed (FIN) and stays empty afterwards. Bytes quiche
/// delivers beyond the caller's buffer are held for the next read,
/// never dropped. FlushAsync sends FIN once — quiche can half-close,
/// unlike a plain Stream — and Dispose marks the adapter disposed
/// without touching the connection (the transport's disconnect
/// function owns connection teardown).
/// </summary>
public sealed class QuicStreamAdapter : Stream
{
    private readonly QuicStream _stream;
    private byte[]? _leftover;
    private int _leftoverOffset;
    private bool _eofSeen;
    private bool _finSent;
    private bool _disposed;

    public QuicStreamAdapter(QuicStream stream)
    {
        _stream = stream;
    }

    public override bool CanRead => !_disposed;

    public override bool CanWrite => !_disposed && !_finSent;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    // Sync flush is a no-op; the async path carries FIN.
    public override void Flush()
    {
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _finSent)
        {
            return;
        }
        _finSent = true;
        await _stream.SendAsync([], fin: true, cancellationToken);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_eofSeen || count == 0)
        {
            return 0;
        }
        if (_leftover is not null)
        {
            int n = Math.Min(count, _leftover.Length - _leftoverOffset);
            Array.Copy(_leftover, _leftoverOffset, buffer, offset, n);
            _leftoverOffset += n;
            if (_leftoverOffset >= _leftover.Length)
            {
                _leftover = null;
                _leftoverOffset = 0;
            }
            return n;
        }
        QuicReadResult result = await _stream.ReadAsync(cancellationToken);
        if (result.Fin)
        {
            _eofSeen = true;
        }
        if (result.Data.Length == 0)
        {
            return 0;
        }
        int read = Math.Min(count, result.Data.Length);
        Array.Copy(result.Data, 0, buffer, offset, read);
        if (read < result.Data.Length)
        {
            _leftover = result.Data;
            _leftoverOffset = read;
        }
        return read;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finSent)
        {
            throw new InvalidOperationException("Stream write side is already finished (FIN sent).");
        }
        byte[] data = buffer.AsSpan(offset, count).ToArray();
        await _stream.SendAsync(data, fin: false, cancellationToken);
    }

    // Sync over async: StreamChannel uses the async methods; the sync
    // path exists only for Stream API completeness.
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
