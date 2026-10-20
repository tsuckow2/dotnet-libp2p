// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Circuit;

/// <summary>
///     Copies two channels into each other until either side ends —
///     the relayed conduit is opaque bytes, so this is the whole data
///     plane of a circuit connection once the handshake is done. The
///     same shape as the pumps in the socket transports.
/// </summary>
internal static class ChannelPump
{
    public static async Task BothWaysAsync(IChannel a, IChannel b)
    {
        Task aToB = CopyAsync(a, b);
        Task bToA = CopyAsync(b, a);
        await Task.WhenAny(aToB, bToA);
        _ = a.CloseAsync();
        _ = b.CloseAsync();
        try
        {
            await Task.WhenAll(aToB, bToA);
        }
        catch
        {
            // A copy loop ending on a torn-down channel is the normal
            // end of a relayed connection.
        }
    }

    private static async Task CopyAsync(IChannel from, IChannel to)
    {
        try
        {
            await foreach (ReadOnlySequence<byte> data in from.ReadAllAsync())
            {
                if (await to.WriteAsync(data) != IOResult.Ok)
                {
                    break;
                }
            }
        }
        catch
        {
            // See above: teardown races are the normal end.
        }
        finally
        {
            _ = to.CloseAsync();
        }
    }

    /// <summary>A task that completes when the channel closes.</summary>
    public static Task WhenClosed(IChannel channel) => Task.Run(async () => await channel);
}
