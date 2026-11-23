// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     What a node knows, for answering other peers' DHT queries: who
///     is close to a key, who provides a block, and which value record
///     a key holds. A client-only node has no table and answers with
///     empty peer sets and no values.
/// </summary>
public interface IKadPeerTable
{
    /// <summary>The known peers closest to the target (a peer id for FIND_NODE, a multihash for GET_PROVIDERS).</summary>
    IReadOnlyList<KadPeer> GetCloserPeers(byte[] target, int count);

    /// <summary>The known providers of the block with this multihash.</summary>
    IReadOnlyList<KadPeer> GetProviders(byte[] key);

    /// <summary>Records a provider for a block (an ADD_PROVIDER announcement, or this node's own providing).</summary>
    void AddProvider(byte[] key, KadPeer provider);

    /// <summary>Records a value for a key (a validated PUT_VALUE announcement, or this node's own publishing).</summary>
    void AddValue(byte[] key, byte[] value, DateTimeOffset expires, string timeReceived);

    /// <summary>The live value record held for a key, if any.</summary>
    ValueStore.DhtValueRecord? GetValue(byte[] key);
}
