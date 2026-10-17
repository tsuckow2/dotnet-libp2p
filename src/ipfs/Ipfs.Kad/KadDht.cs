// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     The Amino Kad DHT: the public IPFS network's Kademlia overlay,
///     spoken by kubo and the other IPFS implementations for content
///     routing (finding who provides a block) and peer routing.
/// </summary>
public static class KadDht
{
    /// <summary>
    ///     The DHT's protocol id on the public network today. It was
    ///     renamed from /ipfs/kad-dht/1.0.0 (still the id in the spec
    ///     and on older nodes); kubo 0.32 advertises only this one.
    /// </summary>
    public const string ProtocolId = "/ipfs/kad/1.0.0";

    /// <summary>The protocol's pre-rename id, spoken by older nodes.</summary>
    public const string LegacyProtocolId = "/ipfs/kad-dht/1.0.0";

    /// <summary>
    ///     The well-known bootstrap peers of the public network, as
    ///     address strings (the same set kubo ships as its defaults):
    ///     DNS-addressed bootstrappers first, then the planet-named
    ///     IPFS Foundation nodes by IPv4. Kept as strings because
    ///     /dnsaddr/ entries denote endpoints only after DNS TXT
    ///     resolution (see KadClient.SeedBootstrapPeersAsync).
    /// </summary>
    public static IReadOnlyList<string> BootstrapPeerAddresses =>
    [
        "/dnsaddr/bootstrap.libp2p.io/p2p/QmNnooDu7bfjPFoTZYxMNLWUQJyrVwtbZg5gBMjTezGAJN",
        "/dnsaddr/bootstrap.libp2p.io/p2p/QmQCU2EcMqAqQPR2i9bChDtGNJchTbq5TbXJJ16u19uLTa",
        "/dnsaddr/bootstrap.libp2p.io/p2p/QmbLHAnMoJPWSCR5Zhtx6BHJX9KiKNN6tpvbUcqanj75Nb",
        "/dnsaddr/bootstrap.libp2p.io/p2p/QmcZf59bWwK5XFi76CZX8cbJ4BhTzzA3gU1ZjYZcYW3dwt",
        "/ip4/104.131.131.82/tcp/4001/p2p/QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ",
        "/ip4/104.236.179.241/tcp/4001/p2p/QmSoLPppuBtQSGwKDZT2M73ULpjvfd3aZ6ha4oFGL1KrGM",
        "/ip4/128.199.219.111/tcp/4001/p2p/QmSoLSafTMBsPKadTEgaXctDQVcqN88CNLHXMkTNwMKPnu",
        "/ip4/104.236.76.40/tcp/4001/p2p/QmSoLV4Bbm51jM9C4gDYZQ9Cy3U6aXMJDAbzgu2fzaDs64",
        "/ip4/178.62.158.247/tcp/4001/p2p/QmSoLer265NRgSp2LA3dPaeykiS1J6DifTC88f5uVQKNAd",
    ];

    /// <summary>The bootstrap peers that parse as multiaddresses (all but the /dnsaddr/ set).</summary>
    public static IReadOnlyList<Multiaddress> BootstrapPeers =>
        BootstrapPeerAddresses
            .Where(addr => !addr.Contains("/dnsaddr/"))
            .Select(addr => (Multiaddress)addr)
            .ToList();
}
