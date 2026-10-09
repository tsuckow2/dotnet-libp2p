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
}
