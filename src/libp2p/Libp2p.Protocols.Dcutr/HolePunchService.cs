// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Dcutr;

/// <summary>
///     Drives a connection upgrade for one relayed connection, in
///     the specification's order:
///
///     1. If the peer's advertised addresses include public ones,
///        it may simply be reachable — try dialing its known direct
///        addresses first (the unilateral upgrade).
///     2. Otherwise run the synchronized DCUtR exchange over the
///        relayed connection, up to the settings' attempt count,
///        each attempt a fresh stream with a fresh RTT measurement.
///
///     Punching is an upgrade attempt, never a gate on talking:
///     the relayed connection is left exactly as it was unless a
///     direct connection replaces it, and failure returns null
///     rather than throwing. Per the specification the caller is
///     the inbound side of the relayed connection (the peer that
///     was reached through the relay); the protocol handler answers
///     correctly on either side of a stream regardless.
/// </summary>
public class HolePunchService(
    IManagedPeerSwitch @switch,
    DcutrProtocolSettings? settings = null,
    ILoggerFactory? loggerFactory = null)
{
    private readonly DcutrProtocolSettings _settings = settings ?? DcutrProtocolSettings.Default;
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<HolePunchService>();

    /// <summary>
    ///     Attempts to upgrade <paramref name="relayedConnection"/>
    ///     to a direct connection to the same proven peer. Returns
    ///     the direct connection — the given one when it is already
    ///     direct, an existing one when the switch already holds
    ///     one, or the one the upgrade established — and null when
    ///     every attempt failed and the relayed connection remains
    ///     the way to the peer.
    /// </summary>
    public async Task<IRemotePeerConnection?> TryUpgradeAsync(
        IRemotePeerConnection relayedConnection, CancellationToken token = default)
    {
        if (HolePunchDialer.IsDirect(relayedConnection))
        {
            return relayedConnection;
        }

        PeerId destination = relayedConnection.ProvenPeerId
            ?? relayedConnection.RemotePeer.Identity.PeerId;

        if (HolePunchDialer.FindDirect(@switch, destination, relayedConnection) is { } existing)
        {
            return existing;
        }

        IReadOnlyList<Multiaddress> known =
            DcutrAddresses.KnownDirectAddresses(relayedConnection.RemotePeer);
        if (known.Count > 0)
        {
            _logger?.LogDebug(
                "DCUtR: trying a unilateral upgrade to {peer} at {count} known direct addresses",
                destination, known.Count);
            IRemotePeerConnection? unilateral = await HolePunchDialer.DialSimultaneouslyAsync(
                @switch, relayedConnection.RemotePeer, destination, known, relayedConnection,
                startDelay: TimeSpan.Zero, window: _settings.AttemptTimeout, token);
            if (unilateral is not null)
            {
                return unilateral;
            }
        }

        for (int attempt = 1; attempt <= _settings.MaxAttempts; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            _logger?.LogDebug("DCUtR: synchronized attempt {attempt}/{max} to {peer}",
                attempt, _settings.MaxAttempts, destination);
            try
            {
                using var attemptTimeout =
                    new CancellationTokenSource(_settings.AttemptTimeout + TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    token, attemptTimeout.Token);
                await relayedConnection.DialAsync<DcutrProtocol>(linked.Token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // A faulted stream — the peer does not speak DCUtR,
                // or the exchange broke — is one failed attempt,
                // not a failed service. The final check below still
                // credits a direct connection that landed meanwhile.
                _logger?.LogDebug("DCUtR: attempt {attempt} to {peer} faulted: {error}",
                    attempt, destination, e.Message);
            }

            if (HolePunchDialer.FindDirect(@switch, destination, relayedConnection) is { } direct)
            {
                return direct;
            }
        }

        return null;
    }
}
