// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     Multiaddrs in their canonical binary form, as DHT (and
///     Identify) messages carry them: a sequence of varint protocol
///     codes, each followed by its value — fixed-size for ip4/ip6/
///     tcp/udp, length-prefixed for dns*/p2p/unix/onion, absent for
///     flag protocols. Multiformats.Address's own byte codec does
///     not round-trip (its writer pads fixed-size values and its
///     reader expects two-byte codes), so the translation goes
///     through the string form, which is well-tested: bytes are
///     parsed to a string here, and the library parses the string.
/// </summary>
internal static class MultiaddressWire
{
    private enum Shape
    {
        None,
        Fixed4,
        Fixed16,
        Port,
        Text,
        PeerId,
        Path,
    }

    private static readonly (string Name, int Code, Shape Shape)[] Protocols =
    [
        ("ip4", 4, Shape.Fixed4),
        ("tcp", 6, Shape.Port),
        ("ip6", 41, Shape.Fixed16),
        ("dns", 53, Shape.Text),
        ("dns4", 54, Shape.Text),
        ("dns6", 55, Shape.Text),
        ("dnsaddr", 56, Shape.Text),
        ("udp", 273, Shape.Port),
        ("p2p-circuit", 290, Shape.None),
        ("unix", 400, Shape.Path),
        ("p2p", 421, Shape.PeerId),
        ("onion", 444, Shape.Text),
        ("onion3", 445, Shape.Text),
        ("quic", 460, Shape.None),
        ("quic-v1", 461, Shape.None),
        ("ws", 477, Shape.None),
        ("wss", 478, Shape.None),
    ];

    public static Multiaddress Decode(byte[] bytes)
    {
        var parts = new List<string>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            int code = (int)ReadVarint(bytes, ref offset);
            (string name, _, Shape shape) = Protocols.FirstOrDefault(p => p.Code == code);
            if (name is null)
            {
                throw new FormatException($"Unknown multiaddr protocol code {code}.");
            }

            parts.Add(name);
            switch (shape)
            {
                case Shape.None:
                    break;
                case Shape.Fixed4:
                    parts.Add(new IPAddress(bytes.AsSpan(offset, 4)).ToString());
                    offset += 4;
                    break;
                case Shape.Fixed16:
                    parts.Add(new IPAddress(bytes.AsSpan(offset, 16)).ToString());
                    offset += 16;
                    break;
                case Shape.Port:
                    parts.Add(((bytes[offset] << 8) | bytes[offset + 1]).ToString());
                    offset += 2;
                    break;
                case Shape.Text:
                case Shape.Path:
                    int textLength = (int)ReadVarint(bytes, ref offset);
                    parts.Add(Encoding.UTF8.GetString(bytes, offset, textLength));
                    offset += textLength;
                    break;
                case Shape.PeerId:
                    int idLength = (int)ReadVarint(bytes, ref offset);
                    parts.Add(new PeerId(bytes[offset..(offset + idLength)]).ToString());
                    offset += idLength;
                    break;
            }
        }

        return Multiaddress.Decode("/" + string.Join("/", parts));
    }

    public static byte[] Encode(Multiaddress address)
    {
        string[] parts = address.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        using var stream = new MemoryStream();
        for (int i = 0; i < parts.Length; i++)
        {
            (string known, int code, Shape shape) = Protocols.FirstOrDefault(p => p.Name == parts[i]);
            if (known is null)
            {
                throw new FormatException($"Unknown multiaddr protocol {parts[i]}.");
            }

            WriteVarint(stream, (ulong)code);
            switch (shape)
            {
                case Shape.None:
                    break;
                case Shape.Fixed4:
                case Shape.Fixed16:
                    byte[] ip = IPAddress.Parse(parts[++i]).GetAddressBytes();
                    stream.Write(ip);
                    break;
                case Shape.Port:
                    int port = int.Parse(parts[++i]);
                    stream.WriteByte((byte)(port >> 8));
                    stream.WriteByte((byte)port);
                    break;
                case Shape.Text:
                case Shape.Path:
                    byte[] text = Encoding.UTF8.GetBytes(parts[++i]);
                    WriteVarint(stream, (ulong)text.Length);
                    stream.Write(text);
                    break;
                case Shape.PeerId:
                    byte[] id = new PeerId(parts[++i]).Bytes;
                    WriteVarint(stream, (ulong)id.Length);
                    stream.Write(id);
                    break;
            }
        }

        return stream.ToArray();
    }

    private static ulong ReadVarint(byte[] bytes, ref int offset)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[offset++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }
}
