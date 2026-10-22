// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     How an address entered a peer's book, or how it was vouched
///     for. Flags: an address usually carries several — it can be
///     enumerated from an interface, later claimed by its owner in
///     Identify, and dialed successfully on top of both.
/// </summary>
[Flags]
public enum AddressSource
{
    None = 0,
    /// <summary>Registered by the application itself.</summary>
    Configured = 1,
    /// <summary>Expanded from a wildcard listener over a local network interface.</summary>
    Interface = 2,
    /// <summary>The owning peer listed it in its own Identify.</summary>
    SelfClaimed = 4,
    /// <summary>Another peer relayed it (a DHT answer, a provider record, a third party's word).</summary>
    Referral = 8,
    /// <summary>Peers reported observing the local peer at this address (its public, post-NAT form).</summary>
    Observed = 16,
}

/// <summary>
///     Per-address accounting for a peer: the evidence the node has
///     collected about one address in the peer's book. Facts only —
///     our own dial outcomes, who vouched for the address and how,
///     and when each last happened. What an address must prove to be
///     advertised, dialed first, or dropped is policy, and lives at
///     the consumers, not here. Counters and timestamps are safe to
///     read and record concurrently.
/// </summary>
public class PeerAddressAccounting
{
    /// <summary>
    ///     Voucher identities tracked per address. Agreement between
    ///     distinct peers is what makes a claim evidence, so the set
    ///     is of peer ids — but it is capped: past the cap, new
    ///     vouchers still refresh freshness without growing memory
    ///     on a hot path.
    /// </summary>
    internal const int MaxTrackedVouchers = 64;

    private int _failedDials;
    private int _successfulDials;
    private long _lastSuccessTicks;
    private long _lastFailureTicks;
    private long _lastConfirmedTicks;
    private long _firstSeenTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int _sources;

    private readonly object _vouchersGate = new();
    private readonly HashSet<string> _vouchers = [];

    public int FailedDials => Volatile.Read(ref _failedDials);
    public int SuccessfulDials => Volatile.Read(ref _successfulDials);

    public DateTimeOffset FirstSeenAt => new(Volatile.Read(ref _firstSeenTicks), TimeSpan.Zero);
    public DateTimeOffset? LastSuccessAt => TicksOrNull(Volatile.Read(ref _lastSuccessTicks));
    public DateTimeOffset? LastFailureAt => TicksOrNull(Volatile.Read(ref _lastFailureTicks));
    /// <summary>The last time any evidence about this address arrived: a dial outcome or a vouch.</summary>
    public DateTimeOffset? LastConfirmedAt => TicksOrNull(Volatile.Read(ref _lastConfirmedTicks));

    public AddressSource Sources => (AddressSource)Volatile.Read(ref _sources);

    /// <summary>Distinct peers that have vouched for this address.</summary>
    public int VouchCount
    {
        get
        {
            lock (_vouchersGate)
            {
                return _vouchers.Count;
            }
        }
    }

    private static DateTimeOffset? TicksOrNull(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

    private static void RaiseMax(ref long field, long value)
    {
        long current;
        while ((current = Volatile.Read(ref field)) < value)
        {
            Interlocked.CompareExchange(ref field, value, current);
        }
    }

    private void Confirm()
    {
        RaiseMax(ref _lastConfirmedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    internal void RecordFailedDial()
    {
        Interlocked.Increment(ref _failedDials);
        RaiseMax(ref _lastFailureTicks, DateTimeOffset.UtcNow.UtcTicks);
        Confirm();
    }

    internal void RecordSuccessfulDial()
    {
        Interlocked.Increment(ref _successfulDials);
        RaiseMax(ref _lastSuccessTicks, DateTimeOffset.UtcNow.UtcTicks);
        Confirm();
    }

    /// <summary>
    ///     Records a peer vouching for this address: the owner
    ///     claiming it in Identify, a third party relaying it, or a
    ///     peer reporting it as the local peer's observed address.
    ///     The voucher is the reporting peer's id — the same peer
    ///     vouching twice is still one voice.
    /// </summary>
    internal void RecordVouch(string voucherPeerId, AddressSource source)
    {
        lock (_vouchersGate)
        {
            if (_vouchers.Count < MaxTrackedVouchers)
            {
                _vouchers.Add(voucherPeerId);
            }
        }
        RecordSource(source);
        Confirm();
    }

    internal void RecordSource(AddressSource source)
    {
        int add = (int)source;
        int current;
        while ((current = Volatile.Read(ref _sources)) != (current | add))
        {
            Interlocked.CompareExchange(ref _sources, current | add, current);
        }
    }

    /// <summary>
    ///     Folds another accounting for the same address into this
    ///     one, as happens when two peer objects for one identity
    ///     merge: the worse failure count stays recorded, the latest
    ///     confirmations win, the earliest first sighting is kept,
    ///     and the voucher sets union.
    /// </summary>
    internal void Absorb(PeerAddressAccounting other)
    {
        int otherFailed = other.FailedDials;
        int currentFailed;
        while ((currentFailed = Volatile.Read(ref _failedDials)) < otherFailed)
        {
            Interlocked.CompareExchange(ref _failedDials, otherFailed, currentFailed);
        }

        int otherSuccessful = other.SuccessfulDials;
        int currentSuccessful;
        while ((currentSuccessful = Volatile.Read(ref _successfulDials)) < otherSuccessful)
        {
            Interlocked.CompareExchange(ref _successfulDials, otherSuccessful, currentSuccessful);
        }

        RaiseMax(ref _lastSuccessTicks, Volatile.Read(ref other._lastSuccessTicks));
        RaiseMax(ref _lastFailureTicks, Volatile.Read(ref other._lastFailureTicks));
        RaiseMax(ref _lastConfirmedTicks, Volatile.Read(ref other._lastConfirmedTicks));

        long otherFirstSeen = Volatile.Read(ref other._firstSeenTicks);
        long currentFirstSeen;
        while ((currentFirstSeen = Volatile.Read(ref _firstSeenTicks)) > otherFirstSeen)
        {
            Interlocked.CompareExchange(ref _firstSeenTicks, otherFirstSeen, currentFirstSeen);
        }

        RecordSource(other.Sources);
        lock (_vouchersGate)
        {
            lock (other._vouchersGate)
            {
                foreach (string voucher in other._vouchers)
                {
                    if (_vouchers.Count >= MaxTrackedVouchers)
                    {
                        break;
                    }
                    _vouchers.Add(voucher);
                }
            }
        }
    }
}
