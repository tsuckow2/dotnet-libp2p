// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Dcutr.Tests;

/// <summary>
///     A book-only peer for policy tests: an identity and an address
///     book, mirroring the in-Core peer's accounting surface without
///     a switch. (The product RemotePeer is internal to Core.)
/// </summary>
internal sealed class FakePeer(Identity identity) : IRemotePeer
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
