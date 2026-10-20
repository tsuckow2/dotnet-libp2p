// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Circuit;

/// <summary>
///     The client side of /libp2p/circuit/relay/0.2.0/hop. The circuit
///     transport queues a job (see <see cref="CircuitJobs"/>) against
///     the relay's peer id and dials this protocol on the relay
///     connection; the instance claims the job, performs the RESERVE
///     or CONNECT exchange, and then either pumps the stream (the
///     connection's data plane) or holds it open (a reservation).
///     The relay server role is deliberately not implemented here.
/// </summary>
public class HopProtocol(CircuitJobs jobs, ILoggerFactory? loggerFactory = null) : IProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<HopProtocol>();

    public string Id => "/libp2p/circuit/relay/0.2.0/hop";

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        PeerId relayId = connection.RemotePeer.Identity.PeerId;
        HopJob job = jobs.ClaimHop(relayId)
            ?? throw new PeerConnectionException($"No circuit job is queued for relay {relayId}.");
        job.RelayConnection = connection;

        try
        {
            if (job.IsReserve)
            {
                await ReserveAsync(channel, job);
            }
            else
            {
                await ConnectAsync(channel, job);
            }
        }
        catch (Exception e)
        {
            job.Established.TrySetException(e);
            job.Stream.TrySetException(e);
            job.PumpTo.TrySetException(e);
            throw;
        }
    }

    public Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        throw new NotSupportedException("This node does not serve as a circuit relay.");
    }

    private async Task ReserveAsync(IChannel channel, HopJob job)
    {
        await CircuitWire.WriteAsync(channel, new HopMessage { Type = HopMessageType.Reserve });
        HopMessage response = await CircuitWire.ReadAsync<HopMessage>(channel);
        if (response.Type != HopMessageType.Status || response.Status != CircuitStatus.Ok || response.Reservation is null)
        {
            throw new PeerConnectionException($"The relay refused the reservation: {response.Status}.");
        }

        job.ReservationExpireUnix = (long)response.Reservation.Expire;
        job.Established.TrySetResult();
        _logger?.LogDebug("Reserved on the relay until {expire}", DateTimeOffset.FromUnixTimeSeconds(job.ReservationExpireUnix));

        // The reservation lives on the connection; keep the stream
        // open with it (strict relays tie the two together), until
        // the transport renews or the listener shuts down.
        await Task.WhenAny(job.Closed.Task, ChannelPump.WhenClosed(channel));
    }

    private async Task ConnectAsync(IChannel channel, HopJob job)
    {
        await CircuitWire.WriteAsync(channel, new HopMessage
        {
            Type = HopMessageType.Connect,
            Peer = new WirePeer { Id = job.Destination!.Bytes },
        });
        HopMessage response = await CircuitWire.ReadAsync<HopMessage>(channel);
        if (response.Type != HopMessageType.Status || response.Status != CircuitStatus.Ok)
        {
            throw new PeerConnectionException($"The relay refused the connection to {job.Destination}: {response.Status}.");
        }

        job.Established.TrySetResult();

        if (job.Raw)
        {
            // Hand the open stream to the raw transport caller; its
            // disconnect completes Closed and ends this protocol.
            job.Stream.TrySetResult(channel);
            await Task.WhenAny(job.Closed.Task, ChannelPump.WhenClosed(channel));
            return;
        }

        // The transport hands over the destination stack's channel
        // once the connection above is ready to be served; pump the
        // relayed stream against it for the connection's lifetime.
        Task<IChannel?> pumpTo = job.PumpTo.Task.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
        if (await Task.WhenAny(pumpTo, ChannelPump.WhenClosed(channel)) == pumpTo && pumpTo.Result is { } upChannel)
        {
            await ChannelPump.BothWaysAsync(channel, upChannel);
        }
    }
}
