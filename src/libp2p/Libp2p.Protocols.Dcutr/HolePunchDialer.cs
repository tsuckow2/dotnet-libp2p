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
    ///     ours or theirs, wins; the losing dials are cancelled (a
    ///     cancelled dial records no failure against its address).
    ///     Returns null when the window closes with no direct
    ///     connection. <paramref name="startDelay"/> postpones our
    ///     dials (the initiator's half-RTT), never the watch: the
    ///     peer's dial can land at any moment.
    /// </summary>
    public static async Task<IRemotePeerConnection?> DialSimultaneouslyAsync(
        IManagedPeerSwitch @switch, IRemotePeer peer, PeerId destination,
        IReadOnlyList<Multiaddress> targets, IRemotePeerConnection relayed,
        TimeSpan startDelay, TimeSpan window, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(window);

        var found = new TaskCompletionSource<IRemotePeerConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task DialOne(Multiaddress target)
        {
            try
            {
                if (startDelay > TimeSpan.Zero)
                {
                    await Task.Delay(startDelay, deadline.Token);
                }

                Multiaddress dialAddress = target.Add<P2P>(destination.ToString());
                IRemotePeerConnection connection =
                    await @switch.DialAsync(peer, dialAddress, deadline.Token);
                found.TrySetResult(connection);
            }
            catch (Exception)
            {
                // A failed candidate is the expected case in a
                // punch — most addresses of a NATed peer go nowhere.
                // The dial already recorded its outcome against the
                // address at the switch's choke point.
            }
        }

        async Task Watch()
        {
            try
            {
                while (!deadline.IsCancellationRequested)
                {
                    if (FindDirect(@switch, destination, relayed) is { } connection)
                    {
                        found.TrySetResult(connection);
                        return;
                    }
                    await Task.Delay(WatchInterval, deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        var running = targets.Select(DialOne).Append(Watch()).ToArray();
        try
        {
            IRemotePeerConnection connection = await found.Task.WaitAsync(deadline.Token);
            return connection;
        }
        catch (OperationCanceledException)
        {
            return FindDirect(@switch, destination, relayed);
        }
        finally
        {
            deadline.Cancel();
            // DialOne and Watch never throw; this just lets them
            // observe the cancellation before the method returns.
            await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static bool SamePeer(PeerId? candidate, PeerId destination)
    {
        return candidate is not null
            && candidate.Bytes.AsSpan().SequenceEqual(destination.Bytes);
    }
}
