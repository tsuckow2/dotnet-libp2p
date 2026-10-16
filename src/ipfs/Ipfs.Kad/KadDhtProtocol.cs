// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     The Kad DHT protocol, channel tier: one query per stream — a
///     Message out, a Message back, stream closed — which is how the
///     Amino network speaks it. The dial half runs the query the
///     <see cref="KadClient"/> queued for the stream's remote peer;
///     the listen half answers from the node's <see cref="IKadPeerTable"/>
///     (empty answers when the node is client-only).
/// </summary>
public class KadDhtProtocol(KadClient? client = null, IKadPeerTable? table = null) : IProtocol
{
    public string Id => KadDht.ProtocolId;

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        if (client is null)
        {
            throw new InvalidOperationException("This node has no KadClient to dial DHT queries with.");
        }

        KadJob? job = client.ClaimJob(context.RemotePeer.Identity.PeerId.Bytes);
        if (job is null)
        {
            throw new InvalidOperationException(
                $"A DHT query stream to {context.RemotePeer.Identity.PeerId} was opened without a queued query.");
        }

        try
        {
            await KadWire.WriteMessageAsync(channel, job.Request);
            Message response = await KadWire.ReadMessageAsync(channel);
            job.Response.TrySetResult(response);
        }
        catch (Exception e)
        {
            job.Response.TrySetException(e);
            throw;
        }
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        Message request = await KadWire.ReadMessageAsync(channel);
        await KadWire.WriteMessageAsync(channel, Answer(request));
    }

    private Message Answer(Message request)
    {
        Message response = KadWire.EmptyResponse(request.type);
        if (table is null)
        {
            return response;
        }

        switch (request.type)
        {
            case MessageType.FindNode:
                response.closerPeers = table.GetCloserPeers(request.key, KadClient.WalkWidth)
                    .Select(peer => peer.ToWire()).ToList();
                break;
            case MessageType.GetProviders:
                response.providerPeers = table.GetProviders(request.key)
                    .Select(peer => peer.ToWire()).ToList();
                response.closerPeers = table.GetCloserPeers(request.key, KadClient.WalkWidth)
                    .Select(peer => peer.ToWire()).ToList();
                break;
        }

        return response;
    }
}
