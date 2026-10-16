// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using ProtoBuf;

namespace Nethermind.Ipfs.Bitswap;

/// <summary>
///     The Bitswap wire format: length-prefixed protobuf Messages
///     (https://github.com/ipfs/specs/blob/main/BITSWAP.md).
/// </summary>
internal static class BitswapWire
{
    public static async Task<Message> ReadMessageAsync(IChannel channel)
    {
        var length = await channel.ReadVarintAsync().ConfigureAwait(false);
        var result = await channel.ReadAsync(length, ReadBlockingMode.WaitAll).ConfigureAwait(false);
        if (result.Result != IOResult.Ok)
        {
            throw new InvalidOperationException("Failed to read data from the channel.");
        }

        return Serializer.Deserialize<Message>(result.Data);
    }
}

[ProtoContract]
internal class Entry
{
    [ProtoMember(1)]
    // changed from string to bytes, it makes a difference in JavaScript
    public byte[] block;      // the block cid (cidV0 in bitswap 1.0.0, cidV1 in bitswap 1.1.0)

    [ProtoMember(2)]
    public int priority = 1;    // the priority (normalized). default to 1

    [ProtoMember(3)]
    public bool cancel;       // whether this revokes an entry
}

[ProtoContract]
internal class Wantlist
{
    [ProtoMember(1)]
    public Entry[] entries;       // a list of wantlist entries

    [ProtoMember(2)]
    public bool full;           // whether this is the full wantlist. default to false
}

[ProtoContract]
internal class Block
{
    [ProtoMember(1)]
    public byte[] prefix;        // CID prefix (cid version, multicodec and multihash prefix (type + length)

    [ProtoMember(2)]
    public byte[] data;
}

[ProtoContract]
internal class Message
{
    [ProtoMember(1)]
    public Wantlist wantlist;

    [ProtoMember(2)]
    public byte[][] blocks;          // used to send Blocks in bitswap 1.0.0

    [ProtoMember(3)]
    public List<Block> payload;         // used to send Blocks in bitswap 1.1.0
}
