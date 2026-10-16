// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using ProtoBuf;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     The Kad DHT wire format: one length-prefixed protobuf Message
///     per stream, request and response
///     (https://github.com/libp2p/specs/blob/master/kad-dht/README.md).
///     Only the fields a routing client needs are modeled — value
///     records (PUT_VALUE/GET_VALUE) are out of scope.
/// </summary>
internal static class KadWire
{
    public static async Task WriteMessageAsync(IChannel channel, Message message)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, message);
        byte[] payload = stream.ToArray();
        await channel.WriteVarintAsync(payload.Length);
        await channel.WriteAsync(new ReadOnlySequence<byte>(payload));
    }

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

    public static Message EmptyResponse(MessageType type)
    {
        return new Message { type = type, closerPeers = [], providerPeers = [] };
    }
}

internal enum MessageType
{
    PutValue = 0,
    GetValue = 1,
    AddProvider = 2,
    GetProviders = 3,
    FindNode = 4,
    Ping = 5,
}

internal enum ConnectionType
{
    NotConnected = 0,
    Connected = 1,
    CanConnect = 2,
    CannotConnect = 3,
}

[ProtoContract]
internal class Peer
{
    [ProtoMember(1)]
    public byte[] id;

    [ProtoMember(2)]
    public List<byte[]> addrs;

    [ProtoMember(3)]
    public ConnectionType connection;
}

[ProtoContract]
internal class Message
{
    [ProtoMember(1)]
    public MessageType type;

    [ProtoMember(2)]
    public byte[] key;

    [ProtoMember(8)]
    public List<Peer> closerPeers;

    [ProtoMember(9)]
    public List<Peer> providerPeers;
}
