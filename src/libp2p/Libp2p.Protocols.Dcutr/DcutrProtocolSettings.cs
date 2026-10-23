// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Dcutr;

/// <summary>
///     Tunables for a hole-punch upgrade attempt. The defaults follow
///     the specification: a bounded window per attempt (the
///     synchronization only lines the dials up once; an address that
///     has not answered in seconds is not about to) and up to three
///     attempts of the full CONNECT/SYNC exchange, each with a fresh
///     RTT measurement.
/// </summary>
public sealed class DcutrProtocolSettings
{
    public static readonly DcutrProtocolSettings Default = new();

    /// <summary>How long one attempt — exchange and dial window — may run before it counts as failed.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Full exchange attempts before the upgrade is given up (the specification's 1 + 2 retries).</summary>
    public int MaxAttempts { get; init; } = 3;
}
