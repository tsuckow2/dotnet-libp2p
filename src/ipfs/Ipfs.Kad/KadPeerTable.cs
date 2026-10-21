// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     The production peer table: closer peers come from the
///     client's own book (everything its walks have learned),
///     providers from the <see cref="ProviderStore"/>. A node
///     registered through AddKadDht answers DHT queries for real;
///     until now it could only ask them.
/// </summary>
public sealed class KadPeerTable(KadClient client, ProviderStore providers) : IKadPeerTable
{
    public IReadOnlyList<KadPeer> GetCloserPeers(byte[] target, int count)
    {
        byte[] hashedTarget = SHA256.HashData(target);
        return client.KnownPeers
            .OrderBy(peer => SHA256.HashData(peer.Id), new XorDistanceComparer(hashedTarget))
            .Take(count)
            .ToArray();
    }

    public IReadOnlyList<KadPeer> GetProviders(byte[] key)
    {
        return providers.GetProviders(key);
    }

    public void AddProvider(byte[] key, KadPeer provider)
    {
        providers.Add(key, provider);
    }
}
