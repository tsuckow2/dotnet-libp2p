// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     What a node knows, for answering other peers' DHT queries: who
///     is close to a key, and who provides a block. A client-only node
///     has no table and answers with empty peer sets.
/// </summary>
public interface IKadPeerTable
{
    /// <summary>The known peers closest to the target (a peer id for FIND_NODE, a multihash for GET_PROVIDERS).</summary>
    IReadOnlyList<KadPeer> GetCloserPeers(byte[] target, int count);

    /// <summary>The known providers of the block with this multihash.</summary>
    IReadOnlyList<KadPeer> GetProviders(byte[] key);
}
