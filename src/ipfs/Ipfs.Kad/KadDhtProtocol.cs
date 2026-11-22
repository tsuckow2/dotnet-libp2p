// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     The Kad DHT protocol, channel tier: one query per stream — a
///     Message out, a Message back, stream closed — which is how the
///     Amino network speaks it. The dial half runs the query the
///     <see cref="KadClient"/> queued for the stream's remote peer;
///     the listen half answers from the node's <see cref="IKadPeerTable"/>
///     (empty answers when the node is client-only). Value records
///     are validated before they are stored, by the validator for
///     the key's namespace — a value with no validator, or one that
///     fails validation, is dropped, never stored.
/// </summary>
public class KadDhtProtocol(KadClient? client = null, IKadPeerTable? table = null, IEnumerable<IDhtValueValidator>? validators = null, ILoggerFactory? loggerFactory = null) : IProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<KadDhtProtocol>();

    public string Id => KadDht.ProtocolId;

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        if (client is null)
        {
            throw new InvalidOperationException("This node has no KadClient to dial DHT queries with.");
        }

        // The job is claimed by the peer id the client dialed, which
        // the remote peer's address book carries in its addresses'
        // terminal /p2p/ parts. The connection's remote identity
        // cannot key the claim: for hashed peer ids (e.g. RSA) it is
        // still the switch's placeholder at this point. TerminalP2P,
        // not Get<P2P>: the book also holds the addresses Identify
        // absorbed, and a peer reachable through a relay advertises
        // /p2p-circuit addresses carrying two /p2p parts — Get<P2P>
        // throws on those, which used to kill the claim (and orphan
        // the job until the client's query deadline) whenever such
        // an address enumerated first.
        List<string> candidates = connection.RemotePeer.Addresses.Keys
            .Select(addr => addr.TerminalP2P()?.ToString())
            .Where(id => id is not null)
            .Distinct()
            .Cast<string>()
            .ToList();
        _logger?.LogDebug("Kad claim for {peer}: {count} candidate id(s) in the address book",
            connection.RemotePeer.Identity.PeerId, candidates.Count);
        KadJob? job = null;
        foreach (string candidate in candidates)
        {
            job = client.ClaimJob(new PeerId(candidate).Bytes);
            if (job is not null)
            {
                break;
            }
        }

        job ??= client.ClaimJob(connection.RemotePeer.Identity.PeerId.Bytes);
        if (job is null)
        {
            throw new InvalidOperationException(
                $"A DHT query stream to {connection.RemotePeer.Identity.PeerId} was opened without a queued query.");
        }

        try
        {
            await KadWire.WriteMessageAsync(channel, job.Request);
            _logger?.LogDebug("Kad query {type} written to {peer}; awaiting the response",
                job.Request.type, connection.RemotePeer.Identity.PeerId);
            Message response = await KadWire.ReadResponseAsync(channel, job.Request.type);
            _logger?.LogDebug("Kad response from {peer}: {closer} closer, {providers} providers",
                connection.RemotePeer.Identity.PeerId, response.closerPeers?.Count ?? 0, response.providerPeers?.Count ?? 0);
            job.Response.TrySetResult(response);
        }
        catch (Exception e)
        {
            _logger?.LogDebug("Kad query to {peer} faulted in the protocol: {error}",
                connection.RemotePeer.Identity.PeerId, e);
            job.Response.TrySetException(e);
            throw;
        }
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        Message request = await KadWire.ReadMessageAsync(channel);
        if (request.type is MessageType.AddProvider && table is not null)
        {
            RecordProvider(request, connection);
        }
        if (request.type is MessageType.PutValue && table is not null)
        {
            RecordValue(request);
        }

        // A node that asks us questions is a peer worth knowing:
        // real DHT servers route by everyone who talks to them.
        if (client is not null && connection.RemotePeer.Addresses.Count > 0)
        {
            client.AddPeer(new KadPeer(
                connection.RemotePeer.Identity.PeerId.Bytes,
                connection.RemotePeer.Addresses.Keys.ToArray()));
        }

        await KadWire.WriteMessageAsync(channel, Answer(request));
    }

    /// <summary>
    ///     Records an ADD_PROVIDER announcement. A peer announces
    ///     only itself: the stored record must name the peer on
    ///     this connection — whose identity the security layer
    ///     proved during establishment — never a third party the
    ///     announcer merely claims to speak for.
    /// </summary>
    private void RecordProvider(Message request, IRemotePeerConnection connection)
    {
        if (request.key is not { Length: > 0 } key)
        {
            return;
        }

        byte[] announcer = connection.RemotePeer.Identity.PeerId.Bytes;
        foreach (Peer wire in request.providerPeers ?? [])
        {
            if (!wire.id.AsSpan().SequenceEqual(announcer))
            {
                continue;
            }

            KadPeer announced = KadPeer.FromWire(wire);
            Multiaddress[] addresses = announced.Addresses
                .Union(connection.RemotePeer.Addresses.Keys)
                .ToArray();
            table!.AddProvider(key, announced with { Addresses = addresses });
        }
    }

    /// <summary>
    ///     Records a PUT_VALUE announcement. A value is stored only
    ///     on its own proof: the validator for the key's namespace
    ///     must accept the bytes, and the key must name the value it
    ///     carries. The announcer's identity is irrelevant — a node
    ///     may publish on the owner's behalf, as the backup agent
    ///     does for a config owner — so, unlike ADD_PROVIDER, there
    ///     is no announcer check here, only the value's.
    /// </summary>
    private void RecordValue(Message request)
    {
        if (request is not { key: { Length: > 0 } key, record: { value: { Length: > 0 } value } })
        {
            return;
        }

        string? ns = DhtValueNamespace.For(key);
        IDhtValueValidator? validator = ns is null ? null
            : validators?.FirstOrDefault(v => v.Namespace == ns);
        if (validator is null)
        {
            _logger?.LogDebug("Kad PUT_VALUE for {key} has no validator for its namespace; dropped",
                Convert.ToHexString(key));
            return;
        }

        if (!validator.TryValidate(key, value, table!.GetValue(key)?.Value, out DateTimeOffset expires, out string? error))
        {
            _logger?.LogDebug("Kad PUT_VALUE for {key} failed validation ({error}); dropped",
                Convert.ToHexString(key), error);
            return;
        }

        table!.AddValue(key, value, expires, request.record!.timeReceived ?? string.Empty);
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
            case MessageType.GetValue:
                if (request.key is { Length: > 0 }
                    && table.GetValue(request.key) is { } held)
                {
                    response.record = new Record
                    {
                        key = request.key,
                        value = held.Value,
                        timeReceived = held.TimeReceived,
                    };
                }
                response.closerPeers = table.GetCloserPeers(request.key, KadClient.WalkWidth)
                    .Select(peer => peer.ToWire()).ToList();
                break;
        }

        return response;
    }
}
