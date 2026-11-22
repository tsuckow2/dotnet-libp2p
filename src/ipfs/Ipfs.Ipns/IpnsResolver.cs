// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Ipfs.Ipns;

/// <summary>Outcome of offering a record to the resolver.</summary>
public enum IpnsAcceptOutcome
{
    /// <summary>Newer than anything held for the name; stored.</summary>
    Accepted,
    /// <summary>Same sequence and same value as the held record; already held, not an error.</summary>
    Duplicate,
    /// <summary>Sequence equal to or lower than the held record (or an equal-sequence conflict); not stored.</summary>
    Stale,
    /// <summary>Failed Verify() (bad signature, wrong key, bad CID, expired, …); not stored.</summary>
    Rejected,
}

/// <summary>
///     Held IPNS records, one per name, with the acceptance rules.
///
///     Ported from the backup prototype's BackupPrototype.Config
///     IpnsResolver.cs — the rules are identical: Verify() first,
///     then sequence. A record that fails any step is dropped, logged
///     with its reason, and never applied, no matter how it arrived
///     (DHT lookup, pubsub announcement, or RPC):
///       1. Verify() — signature, name↔key, value-is-a-CID, validity window.
///       2. Sequence — strictly newer than the record already held for that
///          name. Equal sequence + same value = a duplicate (already held;
///          not an error — re-announcements and re-fetches produce these).
///          Equal sequence + different value = a conflict; the held record
///          wins and the newcomer is rejected as stale. Lower sequence is
///          stale. Ordering is by sequence, never by clock.
///
///     STALENESS IS VISIBLE: Get() returns the record together with
///     when it was fetched, so a caller can show the manifest sequence
///     and the fetch time instead of silently serving an old record.
///     A cached record is fast but can be stale; the resolver never
///     pretends otherwise.
/// </summary>
public sealed class IpnsResolver
{
    private readonly Dictionary<string, (IpnsRecord Record, DateTimeOffset FetchedAt)> _held = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Offers a record. Returns the outcome and a human-readable reason (no secrets).</summary>
    public (IpnsAcceptOutcome Outcome, string Reason) TryAccept(IpnsRecord record, DateTimeOffset now)
    {
        var (ok, reason) = record.Verify(now);
        if (!ok) return (IpnsAcceptOutcome.Rejected, reason);
        lock (_gate)
        {
            if (_held.TryGetValue(record.Name, out var held))
            {
                if (record.Sequence < held.Record.Sequence)
                    return (IpnsAcceptOutcome.Stale,
                        $"stale record: held sequence {held.Record.Sequence} for {record.Name}, got {record.Sequence}");
                if (record.Sequence == held.Record.Sequence)
                {
                    if (record.Value == held.Record.Value)
                        return (IpnsAcceptOutcome.Duplicate,
                            $"duplicate record: sequence {record.Sequence} for {record.Name} already held");
                    return (IpnsAcceptOutcome.Stale,
                        $"conflicting record: held sequence {held.Record.Sequence} for {record.Name} points at {held.Record.Value}, got {record.Value}");
                }
            }
            _held[record.Name] = (record, now);
            return (IpnsAcceptOutcome.Accepted,
                $"accepted record for {record.Name}: sequence {record.Sequence} -> {record.Value}");
        }
    }

    /// <summary>The held record for a name, with when it was fetched (null = never seen). Staleness is the caller's to show.</summary>
    public (IpnsRecord Record, DateTimeOffset FetchedAt)? Get(string name)
    {
        lock (_gate) return _held.TryGetValue(name, out var held) ? held : null;
    }

    /// <summary>All held names (for diagnostics / resolve-all). Returns name → (record, fetchedAt).</summary>
    public IReadOnlyDictionary<string, (IpnsRecord Record, DateTimeOffset FetchedAt)> Held
    {
        get { lock (_gate) return new Dictionary<string, (IpnsRecord, DateTimeOffset)>(_held, StringComparer.Ordinal); }
    }

    /// <summary>Test/restart helper: seeds the held state directly (used when loading a persisted record).</summary>
    public void SeedHeld(IpnsRecord record, DateTimeOffset fetchedAt)
    {
        lock (_gate) _held[record.Name] = (record, fetchedAt);
    }
}
