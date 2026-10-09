// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The local peer: its addresses are the listen set for the switch.
/// </summary>
public class LocalPeer(Identity identity) : ILocalPeer
{
    public Identity Identity { get; } = identity;
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; } = new();

    public PeerAddressAccounting GetOrAddAddress(Multiaddress address)
    {
        return Addresses.GetOrAdd(address, _ => new PeerAddressAccounting());
    }
}
