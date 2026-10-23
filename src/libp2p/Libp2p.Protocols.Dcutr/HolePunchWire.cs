// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using ProtoBuf;

namespace Nethermind.Libp2p.Protocols.Dcutr;

/// <summary>
///     The DCUtR wire message, per the libp2p hole-punching
///     specification: a type and, for CONNECT, the sender's own
///     observed addresses in binary multiaddr form. There is no RTT
///     field on the wire — the initiator measures the relayed
///     stream's round trip locally, between sending its CONNECT and
///     receiving the answer, and times its dial off that.
/// </summary>
[ProtoContract]
public sealed class HolePunchMessage
{
    [ProtoMember(1)] public HolePunchMessageType Type { get; set; }
    [ProtoMember(2)] public List<byte[]> ObsAddrs { get; set; } = [];
}

public enum HolePunchMessageType
{
    Connect = 100,
    Sync = 300,
}

/// <summary>
///     Delimited-protobuf framing for hole-punch messages, mirroring
///     the circuit wire helpers: a varint length prefix, then the
///     serialized message. The specification caps an RPC message at
///     4 KiB; larger ones are refused before any payload is read.
/// </summary>
public static class HolePunchWire
{
    public const int MaxMessageSize = 4 * 1024;

    public static async Task WriteAsync(IChannel channel, HolePunchMessage message)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, message);
        byte[] payload = stream.ToArray();

        await channel.WriteVarintAsync(payload.Length);
        await channel.WriteAsync(new ReadOnlySequence<byte>(payload));
    }

    public static async Task<HolePunchMessage> ReadAsync(IChannel channel, CancellationToken token = default)
    {
        int length = await channel.ReadVarintAsync(token);
        if (length > MaxMessageSize)
        {
            throw new PeerConnectionException(
                $"A DCUtR message of {length} bytes exceeds the {MaxMessageSize}-byte limit.");
        }
        if (length == 0)
        {
            // A message with all-default fields serializes to zero
            // bytes; there is no payload to read. (The channel layer
            // treats a zero-length read as a closed channel, so it
            // must not be attempted.) The protocol layer rejects the
            // resulting typeless message.
            return new HolePunchMessage();
        }

        byte[] payload = (await channel.ReadAsync(length, token: token).OrThrow()).ToArray();
        return Serializer.Deserialize<HolePunchMessage>(new MemoryStream(payload));
    }
}
