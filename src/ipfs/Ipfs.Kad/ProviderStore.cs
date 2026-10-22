// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     Provider records this node holds: who provides which block,
///     learned from ADD_PROVIDER announcements (and from the node's
///     own providing). Records expire — a provider that never
///     re-announces fades out of the answers, which is how the
///     public network forgets departed providers too.
/// </summary>
public sealed class ProviderStore
{
    private static readonly TimeSpan RecordLifetime = TimeSpan.FromHours(24);
    private const int MaxProvidersPerKey = 100;

    private sealed record Entry(KadPeer Peer, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _byKey = new();

    /// <summary>Records (or refreshes) a provider for a block's multihash.</summary>
    public void Add(byte[] key, KadPeer provider)
    {
        ConcurrentDictionary<string, Entry> peers =
            _byKey.GetOrAdd(Convert.ToHexString(key), _ => new ConcurrentDictionary<string, Entry>());
        peers[provider.Key] = new Entry(provider, DateTimeOffset.UtcNow + RecordLifetime);

        if (peers.Count > MaxProvidersPerKey)
        {
            Prune(peers);
            foreach (KeyValuePair<string, Entry> extra in peers.OrderBy(pair => pair.Value.Expires)
                         .Take(peers.Count - MaxProvidersPerKey))
            {
                peers.TryRemove(extra.Key, out _);
            }
        }
    }

    /// <summary>The live providers recorded for a block's multihash.</summary>
    public IReadOnlyList<KadPeer> GetProviders(byte[] key)
    {
        if (!_byKey.TryGetValue(Convert.ToHexString(key), out ConcurrentDictionary<string, Entry>? peers))
        {
            return [];
        }

        Prune(peers);
        return peers.Values.Select(entry => entry.Peer).ToArray();
    }

    /// <summary>One stored provider record, as <see cref="Snapshot"/> reports it.</summary>
    public sealed record ProviderRecord(byte[] Key, KadPeer Provider, DateTimeOffset Expires);

    /// <summary>
    ///     Every live record in the store, expired ones pruned as
    ///     they are passed — the whole of what this node would
    ///     answer GET_PROVIDERS with, for status and diagnostics.
    /// </summary>
    public IReadOnlyList<ProviderRecord> Snapshot()
    {
        var records = new List<ProviderRecord>();
        foreach (KeyValuePair<string, ConcurrentDictionary<string, Entry>> byKey in _byKey)
        {
            Prune(byKey.Value);
            byte[] key = Convert.FromHexString(byKey.Key);
            records.AddRange(byKey.Value.Values.Select(
                entry => new ProviderRecord(key, entry.Peer, entry.Expires)));
        }

        return records;
    }

    private static void Prune(ConcurrentDictionary<string, Entry> peers)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, Entry> pair in peers)
        {
            if (pair.Value.Expires <= now)
            {
                peers.TryRemove(pair.Key, out _);
            }
        }
    }
}
