// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Base;
using Multiformats.Hash;
using SimpleBase;

namespace Nethermind.Ipfs.Bitswap;

/// <summary>
///     A content identifier: a multicodec codec code and a multihash,
///     versioned. CIDv0 is the bare multihash (its codec is implicitly
///     dag-pb); CIDv1 prefixes version and codec as varints. The
///     string form is base58btc for v0 and base32lower multibase
///     ('b...') for v1.
/// </summary>
public readonly record struct Cid
{
    public const ulong DagPbCodec = 0x70;
    public const ulong RawCodec = 0x55;

    /// <summary>The full CID bytes: the bare multihash for v0, varint(version) + varint(codec) + multihash for v1.</summary>
    public required byte[] Bytes { get; init; }

    public required int Version { get; init; }
    public required ulong Codec { get; init; }

    /// <summary>The multihash part: varint(hash code) + varint(digest length) + digest.</summary>
    public required byte[] Multihash { get; init; }

    /// <summary>The hash digest inside the multihash.</summary>
    public byte[] Digest => Multiformats.Hash.Multihash.Decode(Multihash).Digest;

    // A CID is its bytes: two CIDs are equal when the bytes are,
    // not when the arrays happen to be the same object.
    public bool Equals(Cid other) => Bytes.SequenceEqual(other.Bytes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    /// <summary>The CID prefix a Bitswap block carries: everything but the digest.</summary>
    public byte[] Prefix => Bytes[..^Digest.Length];

    public static Cid FromBytes(byte[] bytes)
    {
        // A CIDv0 is a bare multihash; anything else starts with the
        // version varint. A sha2-256 multihash starts 0x12 0x20, which
        // cannot be a version+codec pair, so the two never collide.
        try
        {
            if (Multiformats.Hash.Multihash.Decode(bytes).ToBytes().Length == bytes.Length)
            {
                return new Cid { Bytes = bytes, Version = 0, Codec = DagPbCodec, Multihash = bytes };
            }
        }
        catch (Exception)
        {
            // Not a bare multihash — fall through to the v1 parse.
        }

        int offset = 0;
        ulong version = ReadVarint(bytes, ref offset);
        if (version != 1)
        {
            throw new FormatException($"Unsupported CID version {version}.");
        }

        ulong codec = ReadVarint(bytes, ref offset);
        return new Cid { Bytes = bytes, Version = 1, Codec = codec, Multihash = bytes[offset..] };
    }

    public static Cid Create(ulong codec, byte[] data)
    {
        byte[] multihash = Multiformats.Hash.Multihash.Sum(HashType.SHA2_256, data).ToBytes();
        using var stream = new MemoryStream();
        WriteVarint(stream, 1);
        WriteVarint(stream, codec);
        stream.Write(multihash);
        return FromBytes(stream.ToArray());
    }

    public static Cid Parse(string text)
    {
        if (text.StartsWith('Q'))
        {
            // CIDv0: base58btc of the bare multihash.
            return FromBytes(Base58.Bitcoin.Decode(text));
        }

        byte[] bytes = Multibase.Decode(text, out MultibaseEncoding _);
        return FromBytes(bytes);
    }

    public override string ToString()
    {
        return Version == 0
            ? Base58.Bitcoin.Encode(Bytes)
            : Multibase.Encode(MultibaseEncoding.Base32Lower, Bytes);
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
