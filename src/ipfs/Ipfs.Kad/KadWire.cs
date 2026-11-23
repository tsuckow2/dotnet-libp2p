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
///     Value records ride in <see cref="Message.record"/> (field 3,
///     go's <c>pb/dht.proto</c> Record): a PUT_VALUE carries the record
///     to store, a GET_VALUE answer carries the record held, if any.
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

    /// <summary>
    ///     Reads a response to a request of the given type. A
    ///     zero-length response is the PUT_VALUE acknowledgement: an
    ///     all-default protobuf message (type 0, the default enum
    ///     value, nothing else set) serializes to zero bytes, and a
    ///     zero-length read otherwise reads as a closed channel —
    ///     the same delimited-reader landmine CircuitWire
    ///     special-cases. No other response can be zero-length (every
    ///     other type serializes its type field), so the empty
    ///     response is the acknowledgement, full stop.
    /// </summary>
    public static async Task<Message> ReadResponseAsync(IChannel channel, MessageType requestType)
    {
        var length = await channel.ReadVarintAsync().ConfigureAwait(false);
        if (length == 0)
        {
            return new Message { type = requestType };
        }

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

    [ProtoMember(3)]
    public Record? record;

    [ProtoMember(8)]
    public List<Peer> closerPeers;

    [ProtoMember(9)]
    public List<Peer> providerPeers;
}

/// <summary>
///     A DHT value record, go's <c>pb/dht.proto</c> Record: the key it
///     was stored under, the opaque value bytes, and when the
///     publisher sent it (RFC 3339 UTC; informational — trust comes
///     from the value's own validation, never from this stamp).
/// </summary>
[ProtoContract]
internal class Record
{
    [ProtoMember(1)]
    public byte[] key;

    [ProtoMember(2)]
    public byte[] value;

    [ProtoMember(3)]
    public string timeReceived;
}
