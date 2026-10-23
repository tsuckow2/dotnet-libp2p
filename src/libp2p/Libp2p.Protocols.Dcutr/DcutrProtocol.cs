// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Dcutr;

/// <summary>
///     DCUtR — Direct Connection Upgrade through Relay
///     (https://github.com/libp2p/specs/blob/master/relay/DCUtR.md),
///     protocol <c>/libp2p/dcutr</c>. Two peers holding only a
///     relayed connection synchronize over it and simultaneously
///     dial each other directly; the connection that lands is the
///     upgrade, and the relayed one stays usable throughout — a
///     failed attempt changes nothing.
///
///     Roles follow the specification: the peer that was reached
///     through the relay (the inbound side of the relayed
///     connection) opens the stream — that side is
///     <see cref="DialAsync"/> here. It sends a CONNECT carrying its
///     own observed addresses and starts its RTT timer; the other
///     side answers with a CONNECT carrying its own; the initiator
///     then sends SYNC at once and dials at half the measured RTT,
///     while the responder dials the moment SYNC arrives, so both
///     opens cross. QUIC addresses go first (see
///     <see cref="DcutrAddresses"/>).
/// </summary>
public class DcutrProtocol(
    IServiceProvider serviceProvider,
    DcutrProtocolSettings? settings = null,
    ILoggerFactory? loggerFactory = null) : IProtocol
{
    private readonly DcutrProtocolSettings _settings = settings ?? DcutrProtocolSettings.Default;
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<DcutrProtocol>();
    private IManagedPeerSwitch? _switch;

    public string Id => "/libp2p/dcutr";

    // The builder creates this protocol, so the switch can only be
    // resolved lazily, once construction has finished; resolving it
    // in the constructor deadlocks the DI scope that is building
    // the stack. (The same arrangement as Identify.)
    private IManagedPeerSwitch Switch =>
        _switch ??= serviceProvider.GetRequiredService<IManagedPeerSwitch>();

    /// <summary>
    ///     The initiating side: runs over the relayed connection to
    ///     the destination. Completes when the attempt resolves —
    ///     a direct connection established (by either side's dial)
    ///     or the attempt window closed. Punch failure is a normal
    ///     outcome, not an exception; only a peer that speaks the
    ///     protocol wrongly faults the stream.
    /// </summary>
    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory,
        IRemotePeerConnection connection)
    {
        PeerId destination = DestinationOf(connection);
        _logger?.LogDebug("DCUtR: initiating upgrade to {peer}", destination);

        using var exchangeTimeout = new CancellationTokenSource(_settings.AttemptTimeout);

        IReadOnlyList<Multiaddress> own = DcutrAddresses.OwnAddresses(connection.LocalPeer);
        var timer = Stopwatch.StartNew();
        await HolePunchWire.WriteAsync(channel, ConnectWith(own));
        HolePunchMessage answer = await HolePunchWire.ReadAsync(channel, exchangeTimeout.Token);
        TimeSpan rtt = timer.Elapsed;
        if (answer.Type != HolePunchMessageType.Connect)
        {
            throw new PeerConnectionException(
                $"DCUtR: expected a CONNECT answer from {destination}, got {answer.Type}.");
        }

        IReadOnlyList<Multiaddress> targets =
            DcutrAddresses.ReceivedAddresses(DecodeAll(answer.ObsAddrs), destination);

        await HolePunchWire.WriteAsync(channel,
            new HolePunchMessage { Type = HolePunchMessageType.Sync });

        // The SYNC is already on its way; it reaches the responder
        // in about half the measured round trip, which is when the
        // responder dials — so our dials start at half the RTT and
        // the two opens cross.
        IRemotePeerConnection? direct = await HolePunchDialer.DialSimultaneouslyAsync(
            Switch, connection.RemotePeer, destination, targets, connection,
            startDelay: rtt / 2, window: _settings.AttemptTimeout,
            token: exchangeTimeout.Token);

        _logger?.LogDebug("DCUtR: upgrade to {peer} {outcome}",
            destination, direct is null ? "failed; the relayed connection stays in use" : "established a direct connection");
    }

    /// <summary>
    ///     The answering side: a peer opened a DCUtR stream to us
    ///     over our relayed connection. Answer its CONNECT with our
    ///     own addresses, then dial the moment its SYNC arrives.
    /// </summary>
    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IRemotePeerConnection connection)
    {
        PeerId initiator = DestinationOf(connection);
        _logger?.LogDebug("DCUtR: answering upgrade from {peer}", initiator);

        using var exchangeTimeout = new CancellationTokenSource(_settings.AttemptTimeout);

        HolePunchMessage connect = await HolePunchWire.ReadAsync(channel, exchangeTimeout.Token);
        if (connect.Type != HolePunchMessageType.Connect)
        {
            throw new PeerConnectionException(
                $"DCUtR: expected a CONNECT from {initiator}, got {connect.Type}.");
        }

        IReadOnlyList<Multiaddress> targets =
            DcutrAddresses.ReceivedAddresses(DecodeAll(connect.ObsAddrs), initiator);

        await HolePunchWire.WriteAsync(channel,
            ConnectWith(DcutrAddresses.OwnAddresses(connection.LocalPeer)));

        HolePunchMessage sync = await HolePunchWire.ReadAsync(channel, exchangeTimeout.Token);
        if (sync.Type != HolePunchMessageType.Sync)
        {
            throw new PeerConnectionException(
                $"DCUtR: expected a SYNC from {initiator}, got {sync.Type}.");
        }

        IRemotePeerConnection? direct = await HolePunchDialer.DialSimultaneouslyAsync(
            Switch, connection.RemotePeer, initiator, targets, connection,
            startDelay: TimeSpan.Zero, window: _settings.AttemptTimeout,
            token: exchangeTimeout.Token);

        _logger?.LogDebug("DCUtR: upgrade from {peer} {outcome}",
            initiator, direct is null ? "failed; the relayed connection stays in use" : "established a direct connection");
    }

    private static HolePunchMessage ConnectWith(IReadOnlyList<Multiaddress> ownAddresses)
    {
        return new HolePunchMessage
        {
            Type = HolePunchMessageType.Connect,
            ObsAddrs = ownAddresses.Select(address => MultiaddressWire.Encode(address)).ToList(),
        };
    }

    private static IEnumerable<Multiaddress> DecodeAll(IEnumerable<byte[]> encoded)
    {
        foreach (byte[] bytes in encoded)
        {
            Multiaddress? decoded = null;
            try
            {
                decoded = MultiaddressWire.Decode(bytes);
            }
            catch (Exception e) when (e is FormatException or NotSupportedException)
            {
                // One unparseable claim does not poison the rest.
            }
            if (decoded is not null)
            {
                yield return decoded;
            }
        }
    }

    private static PeerId DestinationOf(IRemotePeerConnection connection)
    {
        return connection.ProvenPeerId
            ?? connection.RemotePeer.Identity?.PeerId
            ?? throw new PeerConnectionException("DCUtR needs a connection whose remote peer is identified.");
    }
}
