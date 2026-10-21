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
public class KadClient : ILibp2pStartup
{
    public const int WalkWidth = 20;   // k: how many closest peers a walk tracks
    public const int Alpha = 3;        // queries in flight per round
    // The public DHT's closest sets are full of ghosts (peers that
    // left without a trace), so the walk budgets generously: many
    // rounds, each query cheap to fail.
    private const int MaxRounds = 32;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger? _logger;
    private readonly KadDhtOptions? _options;
    private readonly object _seedGate = new();
    private Task? _seedTask;
    private IManagedPeerSwitch? _peerSwitch;

    private readonly ConcurrentDictionary<string, KadPeer> _peers = new();
    private readonly ConcurrentDictionary<string, IRemotePeerConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<KadJob>> _jobs = new();

    public KadClient(IServiceProvider serviceProvider, ILoggerFactory? loggerFactory = null, KadDhtOptions? options = null)
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory?.CreateLogger<KadClient>();
        _options = options;
    }

    /// <summary>
    ///     Startup: begins the configured bootstrap seed (see
    ///     <see cref="KadDhtOptions.BootstrapPeers"/>) and returns —
    ///     the seed finishes in the background and every lookup
    ///     awaits it, so a walk can never race ahead of an
    ///     in-flight seed and find an empty book. Without options
    ///     (a directly-registered client) there is nothing to do:
    ///     the client stays unseeded until told otherwise.
    /// </summary>
    public Task StartAsync(CancellationToken token = default)
    {
        BeginSeed();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The one seed task, begun exactly once — by StartAsync,
    ///     or lazily by the first lookup when no startup pass ran.
    ///     It runs uncancelled by its callers: begun once and
    ///     fail-soft per entry, it is cheaper to let it finish
    ///     than to let one caller's cancellation starve every
    ///     later lookup.
    /// </summary>
    private Task BeginSeed()
    {
        if (_options?.BootstrapPeers is not { } bootstrap)
        {
            return Task.CompletedTask;
        }
        lock (_seedGate)
        {
            return _seedTask ??= SeedSetAsync(bootstrap, CancellationToken.None);
        }
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
    ///     (<see cref="KadDht.BootstrapPeers"/>). Only the entries
    ///     that are already concrete addresses are seeded; the
    ///     /dnsaddr/ entries need <see cref="SeedBootstrapPeersAsync"/>.
    /// </summary>
    public void SeedBootstrapPeers()
    {
        foreach (string bootstrap in KadDht.BootstrapPeerAddresses)
        {
            Seed(bootstrap);
        }
    }

    /// <summary>
    ///     Seeds the book with the full public bootstrap set,
    ///     resolving the /dnsaddr/ entries over DNS TXT first. The
    ///     /dnsaddr/ set is the maintained one: the IP-literal
    ///     bootstrappers have mostly retired, and leaning on the one
    ///     survivor makes every walk hostage to a single busy node.
    /// </summary>
    public Task SeedBootstrapPeersAsync(CancellationToken token = default)
    {
        return SeedSetAsync(KadDht.BootstrapPeerAddresses, token);
    }

    /// <summary>
    ///     Seeds the book from a set of address strings, expanding
    ///     /dnsaddr/ entries over DNS TXT first. Fail-soft per
    ///     entry: a bootstrap that cannot be resolved or parsed
    ///     is skipped, never fatal — a partial book is still a
    ///     book, and the public set has dead entries by design.
    /// </summary>
    private async Task SeedSetAsync(IReadOnlyList<string> bootstrap, CancellationToken token)
    {
        foreach (string entry in bootstrap)
        {
            try
            {
                foreach (string expanded in await DnsAddr.ExpandAsync(entry, token))
                {
                    Seed(expanded);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger?.LogDebug("Bootstrap entry {entry} did not seed: {error}", entry, e.Message);
            }
        }
    }

    private void Seed(string address)
    {
        Multiaddress addr;
        string? peerId;
        try
        {
            addr = address;
            peerId = addr.Get<P2P>()?.ToString();
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or InvalidOperationException)
        {
            return;
        }

        if (peerId is null)
        {
            return;
        }

        AddPeer(new KadPeer(new PeerId(peerId).Bytes, [addr]));
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

        // The job wait needs the query's deadline too: a protocol
        // instance that returns without completing its job (a claim
        // miss against the connection's addresses, a stream that
        // stalls in a way the dial never sees) must surface as this
        // query failing — an unbounded wait here parks the whole
        // walk behind it, silently and forever.
        try
        {
            return await job.Response.Task.WaitAsync(timeout.Token);
        }
        catch (Exception e)
        {
            job.Response.TrySetException(e);
            throw;
        }
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
                // The dial itself proved this is the peer: the
                // switch records the address's terminal /p2p as the
                // connection's expected id, and the security layer
                // faults the handshake unless the key it proves
                // names that peer — hashed ids included (the proven
                // key is hashed and compared). No post-dial check
                // belongs here; DHT answers remain hints, and content
                // still verifies by hash at the Bitswap layer.
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

    private ProviderStore? _store;

    private ProviderStore? Store => _store ??= _serviceProvider.GetService<ProviderStore>();

    /// <summary>
    ///     Announces that this node provides the block with this
    ///     multihash: the record is kept locally (peers that ask us
    ///     directly get the answer even if parts of the announce
    ///     fail) and sent by ADD_PROVIDER to the closest peers the
    ///     walk finds — the peers a lookup for this key converges
    ///     on, which is where the network keeps provider records.
    ///     Returns that closest set. Announces are best-effort per
    ///     peer: some implementations close an ADD_PROVIDER stream
    ///     without a response, which reads as a failure here even
    ///     though the record was delivered.
    /// </summary>
    public async Task<IReadOnlyList<KadPeer>> ProvideAsync(byte[] key, CancellationToken token = default)
    {
        KadPeer self = SelfPeer();
        Store?.Add(key, self);

        LookupOutcome outcome = await LookupAsync(MessageType.FindNode, key, token);

        var announcement = new Message
        {
            type = MessageType.AddProvider,
            key = key,
            providerPeers = [self.ToWire()],
        };
        await Task.WhenAll(outcome.Closest
            .Where(peer => peer.Key != self.Key)
            .Select(async peer =>
            {
                try
                {
                    await QueryAsync(peer, announcement, token);
                }
                catch (Exception e)
                {
                    _logger?.LogDebug("Kad provide to {peer} failed: {error}", peer.PeerId, e.Message);
                }
            }));
        return outcome.Closest;
    }

    /// <summary>This node as a Kad peer: its id and the addresses it is reachable at.</summary>
    private KadPeer SelfPeer()
    {
        ILocalPeer localPeer = _serviceProvider.GetService<ILocalPeer>()
            ?? throw new InvalidOperationException(
                "Providing requires the node's local peer to be registered (AddLibp2pLocalPeer).");
        var addresses = new List<Multiaddress>();
        if (localPeer.Address is { } primary)
        {
            addresses.Add(primary);
        }

        addresses.AddRange(localPeer.Addresses.Keys);
        return new KadPeer(localPeer.Identity.PeerId.Bytes, addresses.Distinct().ToArray());
    }

    private sealed record LookupOutcome(IReadOnlyList<KadPeer> Providers, IReadOnlyList<KadPeer> Closest);

    private async Task<LookupOutcome> LookupAsync(MessageType type, byte[] key, CancellationToken token)
    {
        // First use awaits the startup seed (begun by StartAsync,
        // or begun here when no startup pass ran) — a walk that
        // starts from an empty book because DNS was still
        // resolving is a self-inflicted flake.
        await BeginSeed();

        byte[] target = SHA256.HashData(key);
        var queried = new ConcurrentDictionary<string, bool>();
        var failures = new ConcurrentDictionary<string, int>();
        var providers = new ConcurrentDictionary<string, KadPeer>();

        for (int round = 0; round < MaxRounds; round++)
        {
            var closestBefore = _peers.Values
                .OrderBy(peer => SHA256.HashData(peer.Id), new XorDistanceComparer(target))
                .Take(WalkWidth)
                .Select(peer => peer.Key)
                .ToHashSet();

            // A peer whose query fails is not written off on the
            // spot — a slow major peer (the bootstrappers crawl
            // under load) deserves a second chance in a later round —
            // but twice-failed peers stop consuming rounds.
            List<KadPeer> batch = _peers.Values
                .Where(peer => !queried.ContainsKey(peer.Key) && failures.GetValueOrDefault(peer.Key) < 2)
                .OrderBy(peer => SHA256.HashData(peer.Id), new XorDistanceComparer(target))
                .Take(Alpha)
                .ToList();
            if (batch.Count == 0)
            {
                break;
            }

            Message?[] responses = await Task.WhenAll(batch.Select(async peer =>
            {
                try
                {
                    Message response = await QueryAsync(peer, new Message { type = type, key = key }, token);
                    queried[peer.Key] = true;
                    return response;
                }
                catch (Exception e)
                {
                    failures.AddOrUpdate(peer.Key, 1, (_, count) => count + 1);
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

            // Kademlia convergence: the closest set did not change
            // this round and every peer in it has been asked (or has
            // failed twice). Walking on would only interrogate peers
            // farther from the target than the set already settled —
            // on the public network that is minutes of ghost-chasing
            // per key, which made providing a handful of blocks take
            // the better part of an hour.
            List<KadPeer> closestNow = _peers.Values
                .OrderBy(peer => SHA256.HashData(peer.Id), new XorDistanceComparer(target))
                .Take(WalkWidth)
                .ToList();
            bool closestImproved = closestNow.Any(peer => !closestBefore.Contains(peer.Key));
            bool closestOutstanding = closestNow.Any(peer =>
                !queried.ContainsKey(peer.Key) && failures.GetValueOrDefault(peer.Key) < 2);
            if (!closestImproved && !closestOutstanding)
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

}

/// <summary>Compares two keyspace ids by their XOR distance to a target.</summary>
internal sealed class XorDistanceComparer(byte[] target) : IComparer<byte[]>
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
