// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     One queued DHT query: the protocol instance that the switch
///     creates for the query's stream claims it, speaks it, and
///     completes it with the response.
/// </summary>
internal sealed class KadJob(Message request)
{
    public Message Request { get; } = request;
    public TaskCompletionSource<Message> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
///     A routing-only Kad DHT client: it walks the network with
///     FIND_NODE / GET_PROVIDERS queries toward a target, absorbing
///     the closer peers and providers each answer names. Connections
///     are pooled per peer here (the switch builds a connection per
///     dial; a DHT client talks to the same peers repeatedly).
///     Distances are XOR over SHA-256 hashes of the target and of
///     peer ids — the Amino keyspace.
/// </summary>
public class KadClient
{
    public const int WalkWidth = 20;   // k: how many closest peers a walk tracks
    public const int Alpha = 3;        // queries in flight per round
    private const int MaxRounds = 16;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger? _logger;
    private IManagedPeerSwitch? _peerSwitch;

    private readonly ConcurrentDictionary<string, KadPeer> _peers = new();
    private readonly ConcurrentDictionary<string, IRemotePeerConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<KadJob>> _jobs = new();

    public KadClient(IServiceProvider serviceProvider, ILoggerFactory? loggerFactory = null)
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory?.CreateLogger<KadClient>();
    }

    private IManagedPeerSwitch PeerSwitch =>
        _peerSwitch ??= _serviceProvider.GetRequiredService<IManagedPeerSwitch>();

    /// <summary>The peers the client currently knows about.</summary>
    public IReadOnlyCollection<KadPeer> KnownPeers => _peers.Values.ToArray();

    /// <summary>Adds a peer to the book (merging addresses); returns true when the peer is new.</summary>
    public bool AddPeer(KadPeer peer)
    {
        bool isNew = true;
        _peers.AddOrUpdate(peer.Key, peer, (_, existing) =>
        {
            isNew = false;
            Multiaddress[] merged = existing.Addresses.Union(peer.Addresses).ToArray();
            return existing with { Addresses = merged };
        });
        return isNew;
    }

    /// <summary>
    ///     Seeds the book with the public bootstrap peers
    ///     (<see cref="KadDht.BootstrapPeers"/>). Entries this node
    ///     cannot use yet — /dnsaddr/ addresses, which need DNS TXT
    ///     resolution — are skipped, never fatal: a bootstrap list is
    ///     a starting point, not a contract.
    /// </summary>
    public void SeedBootstrapPeers()
    {
        foreach (string bootstrap in KadDht.BootstrapPeerAddresses)
        {
            if (bootstrap.Contains("/dnsaddr/"))
            {
                continue;
            }

            Multiaddress addr = bootstrap;
            string? peerId = addr.Get<P2P>()?.ToString();
            if (peerId is null)
            {
                continue;
            }

            AddPeer(new KadPeer(new PeerId(peerId).Bytes, [addr]));
        }
    }

    internal KadJob? ClaimJob(byte[] remotePeerId)
    {
        return _jobs.TryGetValue(Convert.ToHexString(remotePeerId), out ConcurrentQueue<KadJob>? queue)
            && queue.TryDequeue(out KadJob? job)
            ? job
            : null;
    }

    /// <summary>
    ///     Sends one query to a peer and awaits its response: the job
    ///     is queued for the peer, a stream is opened for the DHT
    ///     protocol over the (pooled) connection, and the protocol
    ///     instance the switch creates for that stream claims the job
    ///     and speaks it.
    /// </summary>
    internal async Task<Message> QueryAsync(KadPeer peer, Message request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(QueryTimeout);

        IRemotePeerConnection connection = await GetConnectionAsync(peer, timeout.Token);
        KadJob job = new(request);
        _jobs.GetOrAdd(peer.Key, _ => new ConcurrentQueue<KadJob>()).Enqueue(job);
        try
        {
            await connection.DialAsync<KadDhtProtocol>(timeout.Token);
        }
        catch (Exception e)
        {
            job.Response.TrySetException(e);
        }

        return await job.Response.Task;
    }

    private async Task<IRemotePeerConnection> GetConnectionAsync(KadPeer peer, CancellationToken token)
    {
        if (_connections.TryGetValue(peer.Key, out IRemotePeerConnection? cached))
        {
            return cached;
        }

        Exception? lastError = null;
        foreach (Multiaddress addr in peer.DialAddresses)
        {
            try
            {
                IRemotePeerConnection connection = await PeerSwitch.DialAsync(addr, token);
                if (!connection.RemotePeer.Identity.PeerId.Bytes.SequenceEqual(peer.Id))
                {
                    await connection.DisconnectAsync();
                    throw new PeerConnectionException(
                        $"Dialed {addr} but reached {connection.RemotePeer.Identity.PeerId}, not {peer.PeerId}.");
                }

                _connections[peer.Key] = connection;
                return connection;
            }
            catch (Exception e)
            {
                lastError = e;
                _logger?.LogDebug("Kad dial to {addr} failed: {error}", addr, e.Message);
            }
        }

        throw new PeerConnectionException($"Could not connect to DHT peer {peer.PeerId}.", lastError);
    }

    /// <summary>Walks toward a peer id; returns the closest peers the walk found.</summary>
    public async Task<IReadOnlyList<KadPeer>> FindNodeAsync(byte[] targetPeerId, CancellationToken token = default)
    {
        LookupOutcome outcome = await LookupAsync(MessageType.FindNode, targetPeerId, token);
        return outcome.Closest;
    }

    /// <summary>Walks toward a block's multihash; returns its providers (possibly none).</summary>
    public async Task<IReadOnlyList<KadPeer>> FindProvidersAsync(byte[] key, CancellationToken token = default)
    {
        LookupOutcome outcome = await LookupAsync(MessageType.GetProviders, key, token);
        return outcome.Providers;
    }

    private sealed record LookupOutcome(IReadOnlyList<KadPeer> Providers, IReadOnlyList<KadPeer> Closest);

    private async Task<LookupOutcome> LookupAsync(MessageType type, byte[] key, CancellationToken token)
    {
        byte[] target = SHA256.HashData(key);
        var queried = new ConcurrentDictionary<string, bool>();
        var providers = new ConcurrentDictionary<string, KadPeer>();

        for (int round = 0; round < MaxRounds; round++)
        {
            List<KadPeer> batch = _peers.Values
                .Where(peer => !queried.ContainsKey(peer.Key))
                .OrderBy(peer => SHA256.HashData(peer.Id), new XorDistanceComparer(target))
                .Take(Alpha)
                .ToList();
            if (batch.Count == 0)
            {
                break;
            }

            Message?[] responses = await Task.WhenAll(batch.Select(async peer =>
            {
                queried[peer.Key] = true;
                try
                {
                    return await QueryAsync(peer, new Message { type = type, key = key }, token);
                }
                catch (Exception e)
                {
                    _logger?.LogDebug("Kad query to {peer} failed: {error}", peer.PeerId, e.Message);
                    return null;
                }
            }));

            bool improved = false;
            foreach (Message? response in responses)
            {
                if (response is null)
                {
                    continue;
                }

                foreach (Peer closer in response.closerPeers ?? [])
                {
                    if (AddPeer(KadPeer.FromWire(closer)))
                    {
                        improved = true;
                    }
                }

                foreach (Peer provider in response.providerPeers ?? [])
                {
                    KadPeer kadPeer = KadPeer.FromWire(provider);
                    AddPeer(kadPeer);
                    providers[kadPeer.Key] = kadPeer;
                }
            }

            // Providers in hand and a round that taught us nobody new:
            // the walk has converged for this key.
            if (!improved && providers.Count > 0)
            {
                break;
            }
        }

        List<KadPeer> closest = _peers.Values
            .OrderBy(peer => SHA256.HashData(peer.Id), new XorDistanceComparer(target))
            .Take(WalkWidth)
            .ToList();
        return new LookupOutcome(providers.Values.ToList(), closest);
    }

    /// <summary>Compares two keyspace ids by their XOR distance to a target.</summary>
    private sealed class XorDistanceComparer(byte[] target) : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y)
        {
            for (int i = 0; i < target.Length; i++)
            {
                int dx = x![i] ^ target[i];
                int dy = y![i] ^ target[i];
                if (dx != dy)
                {
                    return dx.CompareTo(dy);
                }
            }

            return 0;
        }
    }
}
