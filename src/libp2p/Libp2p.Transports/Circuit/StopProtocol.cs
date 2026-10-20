// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Circuit;

/// <summary>
///     /libp2p/circuit/relay/0.2.0/stop, both directions of the client
///     story. Toward a destination (a relay dialing): claim the queued
///     <see cref="StopJob"/>, name the source, and pump the stream
///     against the hop stream the relay holds. As a destination (a
///     relay dialing us): read the CONNECT, refuse it when no circuit
///     listener is registered on this node, otherwise hand the stream
///     to that listener, which accepts it as a new connection — the
///     stack above then runs end-to-end with the source, whose
///     claimed identity the security layer proves or refutes.
/// </summary>
public class StopProtocol(CircuitJobs jobs, ILoggerFactory? loggerFactory = null) : IProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<StopProtocol>();

    public string Id => "/libp2p/circuit/relay/0.2.0/stop";

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        PeerId destinationId = connection.RemotePeer.Identity.PeerId;
        StopJob job = jobs.ClaimStop(destinationId)
            ?? throw new PeerConnectionException($"No stop job is queued for destination {destinationId}.");

        try
        {
            await CircuitWire.WriteAsync(channel, new StopMessage
            {
                Type = StopMessageType.Connect,
                Peer = new WirePeer { Id = job.Source.Bytes },
            });
            StopMessage response = await CircuitWire.ReadAsync<StopMessage>(channel);
            if (response.Type != StopMessageType.Status || response.Status != CircuitStatus.Ok)
            {
                throw new PeerConnectionException($"The destination refused the relayed connection: {response.Status}.");
            }

            job.Established.TrySetResult();
            await ChannelPump.BothWaysAsync(channel, job.UpChannel);
        }
        catch (Exception e)
        {
            job.Established.TrySetException(e);
            throw;
        }
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        StopMessage message = await CircuitWire.ReadAsync<StopMessage>(channel);
        if (message.Type != StopMessageType.Connect || message.Peer is null || message.Peer.Id.Length == 0)
        {
            await CircuitWire.WriteAsync(channel, new StopMessage { Type = StopMessageType.Status, Status = CircuitStatus.MalformedMessage });
            return;
        }

        var source = new PeerId(message.Peer.Id);
        Func<StopAcceptance, Task>? sink = jobs.ListenerFor(connection.LocalPeer.Identity.PeerId);
        if (sink is null)
        {
            await CircuitWire.WriteAsync(channel, new StopMessage { Type = StopMessageType.Status, Status = CircuitStatus.NoReservation });
            return;
        }

        await CircuitWire.WriteAsync(channel, new StopMessage { Type = StopMessageType.Status, Status = CircuitStatus.Ok });

        // The address the source is reachable at: through this relay.
        var sourceAddress = Multiaddress.Decode($"{connection.RemotePeer.Address}/p2p-circuit/p2p/{source}");
        _logger?.LogDebug("Accepting a relayed connection from {source}", source);
        await sink(new StopAcceptance(channel, source, sourceAddress, connection));
    }
}
