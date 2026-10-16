// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace Nethermind.Ipfs.Bitswap;

/// <summary>
///     Block ids: a block is identified by the multihash (sha2-256) of
///     its content, which keeps ids self-verifying. A full CID adds a
///     version and a multicodec code in front of the multihash; the
///     wantlist carries those full CIDs, and translating between them
///     and bare multihashes happens at the session's edge.
/// </summary>
public static class BlockIds
{
    public const int MultihashLength = 34; // 0x12 0x20 + 32-byte digest

    public static byte[] IdFor(byte[] data)
    {
        byte[] digest = SHA256.HashData(data);
        byte[] id = new byte[MultihashLength];
        id[0] = 0x12; // sha2-256
        id[1] = 0x20; // 32 bytes
        digest.CopyTo(id, 2);
        return id;
    }

    /// <summary>The id a received block actually has, from its prefix and content.</summary>
    public static byte[] IdForReceived(byte[] prefix, byte[] data)
    {
        // The sender puts the multihash code+length in the block prefix;
        // the digest part of the id is recomputed from the content, so a
        // corrupted or substituted block never matches the wanted id.
        byte[] digest = SHA256.HashData(data);
        byte[] id = new byte[prefix.Length + digest.Length];
        prefix.CopyTo(id, 0);
        digest.CopyTo(id, prefix.Length);
        return id;
    }

    public static string Key(byte[] id) => Convert.ToHexString(id);
    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}
