// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The local peer: identity and address book. Its addresses are the
///     listen set for the switch; the switch does the listening.
/// </summary>
public class LocalPeer(Identity identity) : ILocalPeer
{
    private Multiaddress? _address;

    public Identity Identity { get; set; } = identity;
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; } = new();

    public Multiaddress Address
    {
        get => _address ?? Addresses.Keys.FirstOrDefault()!;
        set
        {
            _address = value;
            GetOrAddAddress(value);
        }
    }

    public PeerAddressAccounting GetOrAddAddress(Multiaddress address)
    {
        return Addresses.GetOrAdd(address, _ => new PeerAddressAccounting());
    }
}
