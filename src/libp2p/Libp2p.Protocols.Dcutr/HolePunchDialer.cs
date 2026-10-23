// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Dcutr;

/// <summary>
///     Finding and making direct connections to the destination of a
///     relayed connection. A connection counts as the upgrade when
///     it reaches the same proven peer over an address with no relay
///     in it; the relayed connection itself never counts, and the
///     peer's identity is matched on what the security layer proved,
///     not on any claim.
/// </summary>
internal static class HolePunchDialer
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(25);

    public static bool IsDirect(IRemotePeerConnection connection)
    {
        Multiaddress? endpoint = connection.RemoteEndpoint ?? connection.TransportConnection.Address;
        return endpoint is not null
            && endpoint.Protocols.All(protocol => protocol.Name != "p2p-circuit");
    }

    public static bool IsTo(IRemotePeerConnection connection, PeerId destination)
    {
        return SamePeer(connection.ProvenPeerId, destination)
            || SamePeer(connection.RemotePeer.Identity?.PeerId, destination);
    }

    public static IRemotePeerConnection? FindDirect(
        IManagedPeerSwitch @switch, PeerId destination, IRemotePeerConnection? excluding)
    {
        foreach (IRemotePeerConnection connection in @switch.LiveConnections.Keys)
        {
            if (!ReferenceEquals(connection, excluding)
                && IsDirect(connection)
                && IsTo(connection, destination))
            {
                return connection;
            }
        }
        return null;
    }

    /// <summary>
    ///     Dials every target at once — the simultaneous open the
    ///     synchronization exists for — while also watching for the
    ///     peer's own simultaneous dial landing on one of our
    ///     listeners. The first direct connection to the destination,
    ///     ours or theirs, wins. Returns null when the window closes
    ///     with no direct connection. <paramref name="startDelay"/>
    ///     postpones our dials (the initiator's half-RTT), never the
    ///     watch: the peer's dial can land at any moment.
    ///
    ///     No dial made here is EVER cancelled — each gets a
    ///     private source that is never fired, never disposed, and
    ///     not linked to <paramref name="token"/>. The switch
    ///     finalizes a connection under its dial token (identity
    ///     protocols, stream pumps), so cancelling a dial token at
    ///     any point — before, during, or after establishment —
    ///     can leave the connection listed and proven but unable
    ///     to carry a stream. (The first version shared one
    ///     deadline across all dials and cancelled it when a
    ///     winner landed: every punched connection was a zombie,
    ///     and the upgrade "succeeded" without moving a single
    ///     ping.) Callers pass exchange-scoped tokens besides,
    ///     which fire when the exchange ends. The price is that a
    ///     dial to a dead address runs to the transport's own
    ///     timeout instead of the window's; that is the correct
    ///     trade — the window bounds how long this method WAITS,
    ///     not how long dials live, and a dial that lands late is
    ///     just another connection to the destination. Surplus
    ///     connections are kept, never disconnected: disconnecting
    ///     one would leave its accepted twin on the other node
    ///     listed as live but remotely dead.
    /// </summary>
    public static async Task<IRemotePeerConnection?> DialSimultaneouslyAsync(
        IManagedPeerSwitch @switch, IRemotePeer peer, PeerId destination,
        IReadOnlyList<Multiaddress> targets, IRemotePeerConnection relayed,
        TimeSpan startDelay, TimeSpan window, CancellationToken token)
    {
        var dials = targets.Select(DialOne).ToList();

        using var watchSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IRemotePeerConnection?> watch = Watch(watchSource.Token);

        Task windowElapsed = Task.Delay(window, token);
        var pending = dials.Append(watch).Append(windowElapsed).ToList();
        IRemotePeerConnection? winner = null;
        while (pending.Count > 0 && winner is null)
        {
            Task finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            if (ReferenceEquals(finished, windowElapsed))
            {
                break;
            }

            // DialOne and Watch never fault: every outcome is a
            // connection or null.
            winner = await ((Task<IRemotePeerConnection?>)finished).ConfigureAwait(false);
        }

        watchSource.Cancel();
        await ((Task)watch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return winner ?? FindDirect(@switch, destination, relayed);

        async Task<IRemotePeerConnection?> DialOne(Multiaddress target)
        {
            // The source exists only to give this dial a token
            // nothing can fire: it is deliberately leaked, because
            // the token becomes part of the connection's lifetime
            // (see the method's remarks) and disposing the source
            // of a live connection's token is its own hazard.
            var source = new CancellationTokenSource();
            try
            {
                if (startDelay > TimeSpan.Zero)
                {
                    await Task.Delay(startDelay).ConfigureAwait(false);
                }

                Multiaddress dialAddress = target.Add<P2P>(destination.ToString());
                return await @switch.DialAsync(peer, dialAddress, source.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed candidate is the expected case in a
                // punch — most addresses of a NATed peer go nowhere.
                // The dial already recorded its outcome against the
                // address at the switch's choke point.
                return null;
            }
        }

        async Task<IRemotePeerConnection?> Watch(CancellationToken watchToken)
        {
            try
            {
                while (!watchToken.IsCancellationRequested)
                {
                    if (FindDirect(@switch, destination, relayed) is { } connection)
                    {
                        return connection;
                    }

                    await Task.Delay(WatchInterval, watchToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            return FindDirect(@switch, destination, relayed);
        }
    }

    private static bool SamePeer(PeerId? candidate, PeerId destination)
    {
        return candidate is not null
            && candidate.Bytes.AsSpan().SequenceEqual(destination.Bytes);
    }
}
