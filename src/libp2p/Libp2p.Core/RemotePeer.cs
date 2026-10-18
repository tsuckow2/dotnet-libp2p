// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

internal class RemotePeer : IRemotePeer
{
    private Multiaddress? _address;

    public RemotePeer(Identity identity)
    {
        Identity = identity;
        Addresses = new ConcurrentDictionary<Multiaddress, PeerAddressAccounting>();
    }

    public Identity Identity { get; set; }
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; }

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
