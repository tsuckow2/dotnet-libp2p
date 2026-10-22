// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-FileCopyrightText: 2026 Thomas Suckow
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Ipfs.Kad;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Status;

/// <summary>
///     A snapshot of the node's live state at a moment: the local
///     peer and its addresses, the connections currently
///     established, every known peer's address book, and the DHT.
///     Plain data, shaped for JSON — the status server serves it
///     as-is and the status page renders it.
/// </summary>
public sealed record NodeStatusSnapshot(
    DateTimeOffset At,
    LocalNodeStatus Local,
    IReadOnlyList<ConnectionStatus> Connections,
    IReadOnlyList<PeerStatus> Peers,
    DhtStatus? Dht);

/// <summary>The local peer: its identity, what it advertises, and the whole book behind the advertising.</summary>
public sealed record LocalNodeStatus(
    string PeerId,
    IReadOnlyList<string> AdvertisedAddresses,
    IReadOnlyList<AddressStatus> Addresses);

/// <summary>One address and its evidence: where it came from, how dials to it fared, who vouched for it, and when it was last confirmed.</summary>
public sealed record AddressStatus(
    string Address,
    string Sources,
    int SuccessfulDials,
    int FailedDials,
    int VouchCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? LastConfirmedAt);

/// <summary>One live connection: who it reaches (and who the security layer proved), at which endpoints, since when.</summary>
public sealed record ConnectionStatus(
    string RemotePeerId,
    string? ProvenPeerId,
    string? RemoteEndpoint,
    string? LocalEndpoint,
    bool ViaRelay,
    DateTimeOffset EstablishedAt);

/// <summary>One known remote peer and its address book.</summary>
public sealed record PeerStatus(
    string PeerId,
    bool Connected,
    IReadOnlyList<AddressStatus> Addresses);

/// <summary>The Kad DHT's state: the routing book and the provider records this node stores.</summary>
public sealed record DhtStatus(
    IReadOnlyList<KadPeerStatus> KnownPeers,
    IReadOnlyList<ProviderRecordStatus> ProviderRecords);

/// <summary>One peer in the DHT's routing book.</summary>
public sealed record KadPeerStatus(
    string PeerId,
    IReadOnlyList<string> Addresses);

/// <summary>One provider record this node stores for a block.</summary>
public sealed record ProviderRecordStatus(
    string Key,
    string ProviderPeerId,
    IReadOnlyList<string> ProviderAddresses,
    DateTimeOffset ExpiresAt);

/// <summary>
///     Builds <see cref="NodeStatusSnapshot"/>s from the node's
///     live parts. The switch and the DHT are optional: a node
///     without Kad reports no DHT section rather than failing.
/// </summary>
public sealed class NodeStatusProvider(
    ILocalPeer localPeer,
    IManagedPeerSwitch? peerSwitch = null,
    KadClient? kadClient = null,
    ProviderStore? providerStore = null)
{
    public NodeStatusSnapshot Build()
    {
        var liveConnections = peerSwitch?.LiveConnections
            ?? new Dictionary<IRemotePeerConnection, DateTimeOffset>();
        var connectedPeers = liveConnections.Keys
            .Select(connection => connection.RemotePeer)
            .ToHashSet();

        var local = new LocalNodeStatus(
            localPeer.Identity.PeerId.ToString(),
            localPeer.AdvertisedAddresses().Select(address => address.ToString()).ToArray(),
            DescribeBook(localPeer));

        var connections = liveConnections
            .OrderBy(pair => pair.Value)
            .Select(pair => new ConnectionStatus(
                pair.Key.RemotePeer.Identity.PeerId.ToString(),
                pair.Key.ProvenPeerId?.ToString(),
                pair.Key.RemoteEndpoint?.ToString(),
                pair.Key.LocalEndpoint?.ToString(),
                pair.Key.ParentConnection is not null,
                pair.Value))
            .ToArray();

        var peers = (peerSwitch?.RemotePeers.Values ?? [])
            .OrderBy(peer => peer.Identity.PeerId.ToString(), StringComparer.Ordinal)
            .Select(peer => new PeerStatus(
                peer.Identity.PeerId.ToString(),
                connectedPeers.Contains(peer),
                DescribeBook(peer)))
            .ToArray();

        DhtStatus? dht = kadClient is null
            ? null
            : new DhtStatus(
                kadClient.KnownPeers
                    .OrderBy(peer => peer.PeerId.ToString(), StringComparer.Ordinal)
                    .Select(peer => new KadPeerStatus(
                        peer.PeerId.ToString(),
                        peer.Addresses.Select(address => address.ToString()).ToArray()))
                    .ToArray(),
                (providerStore?.Snapshot() ?? [])
                    .OrderBy(record => Convert.ToHexString(record.Key), StringComparer.Ordinal)
                    .Select(record => new ProviderRecordStatus(
                        Convert.ToHexString(record.Key),
                        record.Provider.PeerId.ToString(),
                        record.Provider.Addresses.Select(address => address.ToString()).ToArray(),
                        record.Expires))
                    .ToArray());

        return new NodeStatusSnapshot(DateTimeOffset.UtcNow, local, connections, peers, dht);
    }

    private static AddressStatus[] DescribeBook(IPeer peer) =>
        peer.Addresses
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => Describe(pair.Key, pair.Value))
            .ToArray();

    private static AddressStatus Describe(Multiaddress address, PeerAddressAccounting accounting) =>
        new(
            address.ToString(),
            accounting.Sources.ToString(),
            accounting.SuccessfulDials,
            accounting.FailedDials,
            accounting.VouchCount,
            accounting.FirstSeenAt,
            accounting.LastSuccessAt,
            accounting.LastFailureAt,
            accounting.LastConfirmedAt);
}
