// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     A peer as the DHT knows it: its id (the peer id bytes — the
///     multihash of its public key) and the addresses it was seen at.
/// </summary>
public sealed record KadPeer(byte[] Id, IReadOnlyList<Multiaddress> Addresses)
{
    /// <summary>The id in the base58 form used in /p2p/ address parts.</summary>
    public PeerId PeerId => new(Id);

    /// <summary>A stable dictionary key for this peer.</summary>
    public string Key => Convert.ToHexString(Id);

    /// <summary>The addresses with the /p2p/ part appended, ready to dial.</summary>
    public IEnumerable<Multiaddress> DialAddresses => Addresses.Select(addr =>
        addr.Get<P2P>() is not null
            ? addr
            : (Multiaddress)$"{addr}/p2p/{PeerId}");

    internal Peer ToWire()
    {
        return new Peer
        {
            id = Id,
            addrs = Addresses.Select(MultiaddressWire.Encode).ToList(),
            connection = ConnectionType.CanConnect,
        };
    }

    internal static KadPeer FromWire(Peer peer)
    {
        // An address this node cannot parse (a protocol outside the
        // wire table, or one the address library itself rejects —
        // it has no dnsaddr) is skipped, not fatal: the peer may
        // still be reachable at its other addresses.
        var addresses = new List<Multiaddress>();
        foreach (byte[] bytes in peer.addrs ?? [])
        {
            try
            {
                addresses.Add(MultiaddressWire.Decode(bytes));
            }
            catch (Exception e) when (e is FormatException or NotSupportedException)
            {
            }
        }

        return new KadPeer(peer.id, addresses);
    }
}
