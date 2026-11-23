// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;
using Nethermind.Ipfs.Bitswap;

namespace Nethermind.Ipfs.Ipns;

/// <summary>
///     Canonical dag-cbor encoding (RFC 8943 / IPLD dag-cbor).
///
///     Ported from the backup prototype's BackupPrototype.Config
///     DagCbor.cs — the byte contract is identical: records encoded
///     here decode there and vice versa, which the golden-vector test
///     pins. The prototype's JS management UI implements the same
///     rules; this file is the fork's copy of that contract.
///
///     CANONICALISATION RULES (these are the spec; the encoder enforces them
///     and the decoder rejects input that violates the strict ones):
///       1. Map keys are text strings only in our schemas. Keys sort by the
///          length of their *encoded* form first, then lexicographically by
///          encoded bytes (RFC 8943 §4.2.1). Two keys that encode identically
///          are a duplicate and are rejected on decode.
///       2. Integers use the shortest encoding (0..23 inline, then 1/2/4/8
///          byte forms). Non-minimal integer encodings are rejected on decode.
///       3. No indefinite-length strings, arrays, maps, or byte strings.
///          Every length is definite. Indefinite forms are rejected on decode.
///       4. No floating-point numbers anywhere in our schemas. Timestamps are
///          RFC 3339 UTC strings ("2026-11-21T12:00:00Z", second precision);
///          versions/sequences are integers. A float in input is rejected.
///       5. No tags except tag 42 (CID link): a tag-42 value wraps a byte
///          string whose first byte is 0x00 followed by the raw CID bytes.
///          Any other tag is rejected on decode.
///       6. No duplicate map keys, no trailing bytes after the top-level item.
///       7. Strings are UTF-8; invalid UTF-8 is rejected on decode.
///       8. Byte strings carry raw bytes (payloads, signatures, hashes).
///          CIDs as *values* are text strings in the base32 "bafy…"/"bafk…"
///          form, except where a field is defined as a link.
/// </summary>
public static class DagCbor
{
    // ---- DOM ----

    public abstract record CborValue;
    public sealed record CborInt(long Value) : CborValue;
    public sealed record CborBytes(byte[] Value) : CborValue;
    public sealed record CborText(string Value) : CborValue;
    public sealed record CborArray(IReadOnlyList<CborValue> Items) : CborValue;
    public sealed record CborMap(IReadOnlyList<KeyValuePair<CborValue, CborValue>> Entries) : CborValue;
    public sealed record CborBool(bool Value) : CborValue;
    public sealed record CborNull : CborValue
    {
        public static readonly CborNull Instance = new();
    }
    public sealed record CborTag(ulong Tag, CborValue Value) : CborValue;

    /// <summary>A CID link: tag 42 over bytes 0x00 || cid-bytes.</summary>
    public static CborTag Link(Cid cid) =>
        new(42, new CborBytes([0, .. cid.Bytes]));

    // ---- Encode ----

    public static byte[] Encode(CborValue value)
    {
        using var ms = new MemoryStream();
        Write(value, ms);
        return ms.ToArray();
    }

    private static void Write(CborValue value, Stream out_)
    {
        switch (value)
        {
            case CborInt i:
                if (i.Value >= 0) WriteHeader(0, (ulong)i.Value, out_);
                else WriteHeader(1, (ulong)(-1 - i.Value), out_);
                break;
            case CborBytes b:
                WriteHeader(2, (ulong)b.Value.Length, out_);
                out_.Write(b.Value);
                break;
            case CborText t:
                var utf8 = Encoding.UTF8.GetBytes(t.Value);
                WriteHeader(3, (ulong)utf8.Length, out_);
                out_.Write(utf8);
                break;
            case CborArray a:
                WriteHeader(4, (ulong)a.Items.Count, out_);
                foreach (var item in a.Items) Write(item, out_);
                break;
            case CborMap m:
                // Rule 1: sort by encoded-key length, then lexicographically.
                var sorted = m.Entries
                    .Select(e => (KeyBytes: Encode(e.Key), e.Key, e.Value))
                    .OrderBy(e => e.KeyBytes.Length)
                    .ThenBy(e => e.KeyBytes, ByteArrayComparer.Instance)
                    .ToList();
                WriteHeader(5, (ulong)sorted.Count, out_);
                foreach (var e in sorted)
                {
                    out_.Write(e.KeyBytes);
                    Write(e.Value, out_);
                }
                break;
            case CborBool b:
                out_.WriteByte(b.Value ? (byte)0xf5 : (byte)0xf4);
                break;
            case CborNull:
                out_.WriteByte(0xf6);
                break;
            case CborTag t:
                WriteHeader(6, t.Tag, out_);
                Write(t.Value, out_);
                break;
            default:
                throw new InvalidOperationException($"Unsupported CBOR value: {value.GetType().Name}");
        }
    }

    private static void WriteHeader(int major, ulong value, Stream out_)
    {
        var mt = (byte)(major << 5);
        if (value < 24) out_.WriteByte((byte)(mt | value));
        else if (value <= 0xff) { out_.WriteByte((byte)(mt | 24)); out_.WriteByte((byte)value); }
        else if (value <= 0xffff) { out_.WriteByte((byte)(mt | 25)); WriteBigEndian((ushort)value, out_); }
        else if (value <= 0xffffffff) { out_.WriteByte((byte)(mt | 26)); WriteBigEndian((uint)value, out_); }
        else { out_.WriteByte((byte)(mt | 27)); WriteBigEndian(value, out_); }
    }

    private static void WriteBigEndian(ushort v, Stream s) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    private static void WriteBigEndian(uint v, Stream s) { for (var i = 3; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i))); }
    private static void WriteBigEndian(ulong v, Stream s) { for (var i = 7; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i))); }

    // ---- Decode (strict: rejects every non-canonical form above) ----

    public static CborValue Decode(byte[] bytes)
    {
        var r = new Reader(bytes);
        var value = r.ReadValue();
        if (r.Position != bytes.Length)
            throw new InvalidDataException($"Trailing bytes after CBOR value at offset {r.Position}");
        return value;
    }

    private sealed class Reader(byte[] bytes)
    {
        public int Position { get; private set; }

        public CborValue ReadValue()
        {
            var (major, argument, headerLen) = ReadHeader();
            switch (major)
            {
                case 0:
                    if (argument > long.MaxValue) throw new InvalidDataException("Integer too large for Int64");
                    return new CborInt((long)argument);
                case 1:
                    if (argument >= long.MaxValue) throw new InvalidDataException("Negative integer out of range");
                    return new CborInt(-1 - (long)argument);
                case 2:
                    return new CborBytes(ReadBytes(checked((int)argument)));
                case 3:
                    var textBytes = ReadBytes(checked((int)argument));
                    return new CborText(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(textBytes));
                case 4:
                    return new CborArray(ReadN(checked((int)argument), ReadValue));
                case 5:
                    return ReadMap(checked((int)argument));
                case 6:
                    if (argument != 42) throw new InvalidDataException($"CBOR tag {argument} is not allowed in dag-cbor (only tag 42)");
                    return new CborTag(argument, ReadValue());
                case 7:
                    if (headerLen == 1 && bytes[Position - 1] == 0xf4) return new CborBool(false);
                    if (headerLen == 1 && bytes[Position - 1] == 0xf5) return new CborBool(true);
                    if (headerLen == 1 && bytes[Position - 1] == 0xf6) return CborNull.Instance;
                    throw new InvalidDataException("Unsupported CBOR simple/float value (floats are forbidden in dag-cbor here)");
                default:
                    throw new InvalidDataException($"Unknown CBOR major type {major}");
            }
        }

        private CborMap ReadMap(int count)
        {
            var entries = new List<KeyValuePair<CborValue, CborValue>>(count);
            byte[]? prevKeyBytes = null;
            for (var i = 0; i < count; i++)
            {
                var keyStart = Position;
                var key = ReadValue();
                var keyBytes = bytes[keyStart..Position];
                if (prevKeyBytes != null)
                {
                    // Keys must arrive in canonical order (rule 1) and be unique.
                    var cmp = CompareCanonical(prevKeyBytes, keyBytes);
                    if (cmp >= 0)
                        throw new InvalidDataException("Map keys out of canonical order or duplicated");
                }
                prevKeyBytes = keyBytes;
                entries.Add(new KeyValuePair<CborValue, CborValue>(key, ReadValue()));
            }
            return new CborMap(entries);
        }

        private static int CompareCanonical(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
            return ByteArrayComparer.Instance.Compare(a, b);
        }

        private List<CborValue> ReadN(int count, Func<CborValue> read)
        {
            var list = new List<CborValue>(count);
            for (var i = 0; i < count; i++) list.Add(read());
            return list;
        }

        private byte[] ReadBytes(int count)
        {
            if (count < 0 || Position + count > bytes.Length)
                throw new InvalidDataException("CBOR length exceeds input");
            var slice = bytes[Position..(Position + count)];
            Position += count;
            return slice;
        }

        private (int Major, ulong Argument, int HeaderLen) ReadHeader()
        {
            if (Position >= bytes.Length) throw new InvalidDataException("Unexpected end of CBOR input");
            var start = Position;
            var b = bytes[Position++];
            var major = b >> 5;
            var ai = b & 0x1f;
            if (ai == 31) throw new InvalidDataException("Indefinite-length CBOR items are forbidden in dag-cbor");
            ulong argument;
            switch (ai)
            {
                case < 24: argument = (ulong)ai; break;
                case 24:
                    argument = ReadByte();
                    if (argument < 24) throw new InvalidDataException("Non-minimal CBOR integer encoding");
                    break;
                case 25:
                    argument = ReadUInt16();
                    if (argument <= 0xff) throw new InvalidDataException("Non-minimal CBOR integer encoding");
                    break;
                case 26:
                    argument = ReadUInt32();
                    if (argument <= 0xffff) throw new InvalidDataException("Non-minimal CBOR integer encoding");
                    break;
                case 27:
                    argument = ReadUInt64();
                    if (argument <= 0xffffffff) throw new InvalidDataException("Non-minimal CBOR integer encoding");
                    break;
                default:
                    throw new InvalidDataException($"Reserved CBOR additional information {ai}");
            }
            return (major, argument, Position - start);
        }

        private byte ReadByte()
        {
            if (Position >= bytes.Length) throw new InvalidDataException("Unexpected end of CBOR input");
            return bytes[Position++];
        }

        private ushort ReadUInt16() => (ushort)((ReadByte() << 8) | ReadByte());
        private uint ReadUInt32() { uint v = 0; for (var i = 0; i < 4; i++) v = (v << 8) | ReadByte(); return v; }
        private ulong ReadUInt64() { ulong v = 0; for (var i = 0; i < 8; i++) v = (v << 8) | ReadByte(); return v; }
    }

    /// <summary>Unsigned lexicographic byte comparison.</summary>
    public sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? a, byte[]? b)
        {
            if (a == null || b == null) return (a == null).CompareTo(b == null);
            var n = Math.Min(a.Length, b.Length);
            for (var i = 0; i < n; i++)
            {
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
