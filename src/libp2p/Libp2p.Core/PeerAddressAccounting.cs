// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     Per-address accounting for a peer: what the switch has learned about
///     dialing this address. Counters are safe to read and record
///     concurrently.
/// </summary>
public class PeerAddressAccounting
{
    private int _failedDials;

    public int FailedDials => Volatile.Read(ref _failedDials);

    internal void RecordFailedDial()
    {
        Interlocked.Increment(ref _failedDials);
    }

    /// <summary>
    ///     Raises this count to at least another accounting's: when two
    ///     peer objects for one identity merge, the failures recorded
    ///     against an address on either side stay recorded.
    /// </summary>
    internal void Absorb(PeerAddressAccounting other)
    {
        int otherFailed = other.FailedDials;
        int current;
        while ((current = Volatile.Read(ref _failedDials)) < otherFailed)
        {
            Interlocked.CompareExchange(ref _failedDials, otherFailed, current);
        }
    }
}
