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

    private RemotePeer(Identity identity, ConcurrentDictionary<Multiaddress, PeerAddressAccounting> addresses)
    {
        Identity = identity;
        Addresses = addresses;
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

    /// <summary>
    ///     A fork for a new peer context: same peer, same address book.
    /// </summary>
    internal RemotePeer Fork()
    {
        return new RemotePeer(Identity, Addresses);
    }
}
