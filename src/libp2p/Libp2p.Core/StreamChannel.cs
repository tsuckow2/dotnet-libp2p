using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public class StreamChannel(Stream stream) : IChannel
{
    public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        if (length == 0)
        {
            return ReadResult.Empty;
        }
        var buffer = new byte[length];

        try
        {
            var readlength = await stream.ReadAsync(buffer, token);


            if (readlength == 0)
            {
                return ReadResult.Ended;
            }
            ReadOnlySequence<byte> bytes = new(buffer, 0, readlength);
            return ReadResult.Ok(bytes);
        }
        catch (TaskCanceledException)
        {
            return ReadResult.Cancelled;
        }
    }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        try
        {
            await stream.WriteAsync(bytes.ToArray(), token);
        }
        catch (TaskCanceledException)
        {
            return IOResult.Cancelled;
        }
        catch (ObjectDisposedException)
        {
            return IOResult.Ended;
        }
        return IOResult.Ok;
    }

    private readonly TaskCompletionSource _completion = new();

    // A plain Stream cannot half-close; flushing is the faithful equivalent.
    public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
    {
        try
        {
            await stream.FlushAsync(token);
        }
        catch (TaskCanceledException)
        {
            return IOResult.Cancelled;
        }
        catch (ObjectDisposedException)
        {
            return IOResult.Ended;
        }
        return IOResult.Ok;
    }

    public TaskAwaiter GetAwaiter() => _completion.Task.GetAwaiter();

    public async ValueTask CloseAsync()
    {
        await stream.DisposeAsync();
        _completion.TrySetResult();
    }
}