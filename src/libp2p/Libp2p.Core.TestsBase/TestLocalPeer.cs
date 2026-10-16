// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestLocalPeer : ILocalPeer
{
    public TestLocalPeer()
    {
        Identity = new(Enumerable.Repeat((byte)42, 32).ToArray());
        Address = $"/p2p/{Identity.PeerId}";
    }

    public Identity Identity { get; set; }
    public Multiaddress Address { get; set; }
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; } = new();

    public PeerAddressAccounting GetOrAddAddress(Multiaddress address)
    {
        return Addresses.GetOrAdd(address, _ => new PeerAddressAccounting());
    }
}

public class TestRemotePeer : IRemotePeer
{
    public TestRemotePeer(Multiaddress addr)
    {
        Identity = TestPeers.Identity(addr);
        Address = addr;
    }

    public Identity Identity { get; set; }
    public Multiaddress Address { get; set; }
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; } = new();

    public PeerAddressAccounting GetOrAddAddress(Multiaddress address)
    {
        return Addresses.GetOrAdd(address, _ => new PeerAddressAccounting());
    }
}
