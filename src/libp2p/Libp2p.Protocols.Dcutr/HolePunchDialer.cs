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
    ///     Each dial gets its own cancellation source, deliberately
    ///     NOT linked to <paramref name="token"/>: a stack
    ///     connection's stream layer lives under the token its dial
    ///     was made with, so cancelling after establishment leaves
    ///     the connection listed and proven but unable to carry a
    ///     stream. (One shared deadline, cancelled the moment a
    ///     winner landed, zombified every connection a punch
    ///     produced — the upgrade "succeeded" and then could not
    ///     move a single ping. Linking would be no better: callers
    ///     pass exchange-scoped tokens — the DCUtR channel's own —
    ///     which fire the moment the exchange ends.) The caller's
    ///     token therefore only aborts the race while it runs, via
    ///     a registration that dies with this method; the winner's
    ///     source is then merely disarmed and left undisposed —
    ///     the token is part of the connection's lifetime now.
    ///     Losing dials are cancelled while still in flight (a
    ///     cancelled dial records no failure against its address);
    ///     a loser that established anyway is disconnected
    ///     outright, never abandoned.
    /// </summary>
    public static async Task<IRemotePeerConnection?> DialSimultaneouslyAsync(
        IManagedPeerSwitch @switch, IRemotePeer peer, PeerId destination,
        IReadOnlyList<Multiaddress> targets, IRemotePeerConnection relayed,
        TimeSpan startDelay, TimeSpan window, CancellationToken token)
    {
        var attempts = new List<DialAttempt>();
        foreach (Multiaddress target in targets)
        {
            var attempt = new DialAttempt(new CancellationTokenSource());
            attempt.Source.CancelAfter(window);
            attempts.Add(attempt);
            attempt.Task = DialOne(attempt, target);
        }

        using var abortRegistration = token.Register(() =>
        {
            foreach (DialAttempt attempt in attempts)
            {
                attempt.Source.Cancel();
            }
        });

        using var watchSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IRemotePeerConnection?> watch = Watch(watchSource.Token);

        var pending = attempts.Select(a => a.Task).Append(watch).ToList();
        IRemotePeerConnection? winner = null;
        while (pending.Count > 0 && winner is null)
        {
            Task<IRemotePeerConnection?> finished =
                await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            // DialOne and Watch never fault: every outcome is a
            // connection or null.
            winner = await finished.ConfigureAwait(false);
        }

        watchSource.Cancel();
        await ((Task)watch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        IRemotePeerConnection? result =
            winner ?? FindDirect(@switch, destination, relayed);
        await RetireAttempts(attempts, result).ConfigureAwait(false);
        return result;

        async Task<IRemotePeerConnection?> DialOne(DialAttempt attempt, Multiaddress target)
        {
            try
            {
                if (startDelay > TimeSpan.Zero)
                {
                    await Task.Delay(startDelay, attempt.Source.Token).ConfigureAwait(false);
                }

                Multiaddress dialAddress = target.Add<P2P>(destination.ToString());
                return await @switch.DialAsync(peer, dialAddress, attempt.Source.Token)
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
            using var windowSource = CancellationTokenSource.CreateLinkedTokenSource(watchToken);
            windowSource.CancelAfter(window);
            try
            {
                while (!windowSource.IsCancellationRequested)
                {
                    if (FindDirect(@switch, destination, relayed) is { } connection)
                    {
                        return connection;
                    }

                    await Task.Delay(WatchInterval, windowSource.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            return FindDirect(@switch, destination, relayed);
        }
    }

    /// <summary>
    ///     Settles every dial of a finished simultaneous attempt:
    ///     the winner's source is disarmed (never cancelled, never
    ///     disposed — see <see cref="DialSimultaneouslyAsync"/>);
    ///     every other dial is cancelled, and any connection it
    ///     nevertheless established — then or a moment later — is
    ///     disconnected rather than left behind.
    /// </summary>
    private static async Task RetireAttempts(
        List<DialAttempt> attempts, IRemotePeerConnection? winner)
    {
        foreach (DialAttempt attempt in attempts)
        {
            if (attempt.Task is { IsCompleted: true }
                && attempt.Task.Result is { } established)
            {
                if (ReferenceEquals(established, winner))
                {
                    attempt.Source.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                else
                {
                    attempt.Source.Cancel();
                    await SafeDisconnect(established).ConfigureAwait(false);
                }

                continue;
            }

            attempt.Source.Cancel();
            if (attempt.Task is { } inFlight)
            {
                // A dial cancelled in the same instant it
                // establishes can still deliver a connection;
                // retire it whenever it lands.
                _ = inFlight.ContinueWith(
                    async finished =>
                    {
                        if (finished.Status == TaskStatus.RanToCompletion
                            && finished.Result is { } late
                            && !ReferenceEquals(late, winner))
                        {
                            await SafeDisconnect(late).ConfigureAwait(false);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private static async Task SafeDisconnect(IRemotePeerConnection connection)
    {
        try
        {
            await connection.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown of a surplus connection must never fault
            // the upgrade that replaced it.
        }
    }

    private sealed class DialAttempt(CancellationTokenSource source)
    {
        public CancellationTokenSource Source { get; } = source;
        public Task<IRemotePeerConnection?>? Task { get; set; }
    }

    private static bool SamePeer(PeerId? candidate, PeerId destination)
    {
        return candidate is not null
            && candidate.Bytes.AsSpan().SequenceEqual(destination.Bytes);
    }
}
