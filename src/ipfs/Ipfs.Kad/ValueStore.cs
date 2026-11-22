// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     DHT value records this node holds: one live record per key,
///     learned from PUT_VALUE announcements (and from the node's own
///     publishing). Unlike provider records — many peers per block —
///     a key holds a single value, and it expires on the validity
///     its own validator reported, not on a fixed lifetime: a record
///     whose validity has passed is not served, and a newer valid
///     record replaces an older one for the same key. Records arrive
///     here only through validation (the protocol layer, or the
///     client's publish path) — the store itself trusts its callers.
/// </summary>
public sealed class ValueStore
{
    private const int MaxKeys = 10000;

    private sealed record Entry(byte[] Value, DateTimeOffset Expires, string TimeReceived);

    private readonly ConcurrentDictionary<string, Entry> _byKey = new();

    /// <summary>Records (or replaces) the value for a key.</summary>
    public void Put(byte[] key, byte[] value, DateTimeOffset expires, string timeReceived)
    {
        _byKey[Convert.ToHexString(key)] = new Entry(value, expires, timeReceived);

        if (_byKey.Count > MaxKeys)
        {
            foreach (KeyValuePair<string, Entry> extra in _byKey
                         .OrderBy(pair => pair.Value.Expires)
                         .Take(_byKey.Count - MaxKeys))
            {
                _byKey.TryRemove(extra.Key, out _);
            }
        }
    }

    /// <summary>The live record for a key, if one is held and unexpired.</summary>
    public DhtValueRecord? Get(byte[] key)
    {
        if (!_byKey.TryGetValue(Convert.ToHexString(key), out Entry? entry))
        {
            return null;
        }

        if (entry.Expires <= DateTimeOffset.UtcNow)
        {
            _byKey.TryRemove(Convert.ToHexString(key), out _);
            return null;
        }

        return new DhtValueRecord(key, entry.Value, entry.Expires, entry.TimeReceived);
    }

    /// <summary>One stored value record, as <see cref="Snapshot"/> reports it.</summary>
    public sealed record DhtValueRecord(byte[] Key, byte[] Value, DateTimeOffset Expires, string TimeReceived);

    /// <summary>
    ///     Every live record in the store, expired ones pruned as
    ///     they are passed — the whole of what this node would
    ///     answer GET_VALUE with, for status and diagnostics.
    /// </summary>
    public IReadOnlyList<DhtValueRecord> Snapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var records = new List<DhtValueRecord>();
        foreach (KeyValuePair<string, Entry> pair in _byKey)
        {
            if (pair.Value.Expires <= now)
            {
                _byKey.TryRemove(pair.Key, out _);
                continue;
            }

            records.Add(new DhtValueRecord(Convert.FromHexString(pair.Key), pair.Value.Value, pair.Value.Expires, pair.Value.TimeReceived));
        }

        return records;
    }
}
