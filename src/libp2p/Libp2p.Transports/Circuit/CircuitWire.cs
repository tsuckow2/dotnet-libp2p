// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using ProtoBuf;

namespace Nethermind.Libp2p.Protocols.Circuit;

/// <summary>
///     The Circuit Relay v2 wire messages (hop and stop), field numbers
///     and status codes per the libp2p circuit-v2 specification,
///     carried as varint-length-prefixed protobuf — the framing every
///     delimited libp2p protocol uses.
/// </summary>
[ProtoContract]
public sealed class HopMessage
{
    [ProtoMember(1)] public HopMessageType Type { get; set; }
    [ProtoMember(2)] public WirePeer? Peer { get; set; }
    [ProtoMember(3)] public WireReservation? Reservation { get; set; }
    [ProtoMember(4)] public WireLimit? Limit { get; set; }
    [ProtoMember(5)] public CircuitStatus Status { get; set; }
}

[ProtoContract]
public sealed class StopMessage
{
    [ProtoMember(1)] public StopMessageType Type { get; set; }
    [ProtoMember(2)] public WirePeer? Peer { get; set; }
    [ProtoMember(3)] public WireLimit? Limit { get; set; }
    [ProtoMember(4)] public CircuitStatus Status { get; set; }
}

[ProtoContract]
public sealed class WirePeer
{
    [ProtoMember(1)] public byte[] Id { get; set; } = [];
    [ProtoMember(2)] public List<byte[]> Addrs { get; set; } = [];
}

[ProtoContract]
public sealed class WireReservation
{
    [ProtoMember(1)] public ulong Expire { get; set; }
    [ProtoMember(2)] public List<byte[]> Addrs { get; set; } = [];
    [ProtoMember(3)] public byte[]? Voucher { get; set; }
}

[ProtoContract]
public sealed class WireLimit
{
    [ProtoMember(1)] public uint Duration { get; set; }
    [ProtoMember(2)] public ulong Data { get; set; }
}

public enum HopMessageType
{
    Reserve = 0,
    Connect = 1,
    Status = 2,
}

public enum StopMessageType
{
    Connect = 0,
    Status = 1,
}

public enum CircuitStatus
{
    Unused = 0,
    Ok = 100,
    ReservationRefused = 200,
    ResourceLimitExceeded = 201,
    PermissionDenied = 202,
    ConnectionFailed = 203,
    NoReservation = 204,
    MalformedMessage = 400,
    UnexpectedMessage = 401,
}

/// <summary>
///     Delimited-protobuf framing for circuit messages, mirroring the
///     helpers the other hand-written wire formats in this codebase
///     use: a varint length prefix, then the serialized message.
/// </summary>
public static class CircuitWire
{
    public static async Task WriteAsync<T>(IChannel channel, T message) where T : class
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, message);
        byte[] payload = stream.ToArray();

        await channel.WriteVarintAsync(payload.Length);
        await channel.WriteAsync(new ReadOnlySequence<byte>(payload));
    }

    public static async Task<T> ReadAsync<T>(IChannel channel, CancellationToken token = default) where T : class, new()
    {
        int length = await channel.ReadVarintAsync(token);
        if (length == 0)
        {
            // A message whose fields are all default values (a bare
            // RESERVE, say) serializes to zero bytes; there is no
            // payload to read. (The channel layer treats a zero-length
            // read as a closed channel, so it must not be attempted.)
            return new T();
        }

        byte[] payload = (await channel.ReadAsync(length, token: token).OrThrow()).ToArray();
        return Serializer.Deserialize<T>(new MemoryStream(payload));
    }
}
