// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Globalization;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Enums;
using BitswapCid = Nethermind.Ipfs.Bitswap.Cid;

namespace Nethermind.Ipfs.Ipns;

/// <summary>
///     An IPNS record: the signed, sequenced pointer that makes a
///     changing value addressable by one stable name. The name IS an
///     Ed25519 public key (its PeerID form, "12D3KooW…"); the value
///     is the current manifest CID; the record carries a monotonic
///     sequence and a validity window (expiry), and is signed by the
///     name's private key.
///
///     Ported from the backup prototype's BackupPrototype.Config
///     IpnsRecord.cs — the format, the field names, the signature
///     input, and the resolver rules are identical, so a record
///     published by the prototype verifies here and vice versa. The
///     only differences are the APIs it signs and verifies with
///     (the fork's <see cref="Identity"/> instead of raw BouncyCastle
///     parameters) and the absence of the JSON view, which is the
///     prototype's RPC transport, not this library's.
///
///     ENCODING: canonical dag-cbor, exactly the P1 rules
///     (DagCbor.cs): map keys are text, length-first-then-
///     lexicographic sort, shortest integers, no floats, no
///     indefinite lengths, no tags except tag 42 (unused here),
///     timestamps are RFC 3339 UTC strings, second precision.
///
///     SIGNATURE INPUT: canonical dag-cbor of the record map with the
///     "signature" field omitted. The signature covers the value,
///     sequence, validity, TTL, and public key together, so none of
///     them can be altered without invalidating it.
///
///     FIELDS (dag-cbor map):
///       name           text   PeerID of the IPNS key ("12D3KooW…"). The name.
///       value          text   Current manifest CID ("bafyrei…").
///       sequence       int    Monotonic, first record = 1, +1 per publish.
///                            Ordering is by sequence, never by clock.
///       validity       text   RFC 3339 UTC expiry ("2026-11-22T12:00:00Z").
///                            A record is trusted only until it passes.
///       validity_type  text   Always "eol" (end-of-life; mirrors go-ipns
///                            ValidityType EOL). Reserved for future types.
///       ttl_seconds    int    How long a resolver may cache the record
///                            (3600). The validity window still bounds trust.
///       public_key     bytes  32-byte Ed25519 public key the name derives
///                            from. Embedded so a resolver that never saw the
///                            key can still verify the signature — the record
///                            is self-authenticating (name == PeerID(key)).
///       signature      bytes  64-byte Ed25519 over SignatureInput().
///
///     RESOLVER RULES (IpnsResolver): accept only if the signature verifies
///     against the embedded key, the embedded key's PeerID equals the name,
///     the value parses as a CID, the validity window has not expired, and
///     the sequence is strictly newer than any record already held for that
///     name. Equal sequence with the same value is a duplicate (already held,
///     not an error); equal sequence with a different value is a conflict and
///     is rejected as stale. Anything failing any check is dropped and logged,
///     never applied — no matter how it arrived (DHT, pubsub, or RPC).
///
///     DHT KEY: the standard IPNS DHT key is the bytes "/ipns/" followed
///     by the binary PeerID (the base58btc-decoded multihash).
///     DhtKey() computes it.
///
///     KEY CUSTODY: the IPNS key is a DEDICATED Ed25519 keypair, separate
///     from the node identity key (one key, one job). A delegate — the
///     backup agent publishing on the owner's behalf — holds it; the
///     owner rotates it by re-pointing. A compromised IPNS key can point
///     the name at a stale or empty manifest — it cannot forge config,
///     because modules and manifests are signed by their authors.
/// </summary>
public sealed record IpnsRecord
{
    /// <summary>First sequence published for a name.</summary>
    public const long FirstSequence = 1;

    /// <summary>Default validity window for a freshly published record: 24 hours (go-ipns default EOL).</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(24);

    /// <summary>Default cache TTL advertised in the record: 1 hour. The validity window still bounds trust.</summary>
    public const long DefaultTtlSeconds = 3600;

    public const string ValidityTypeEol = "eol";

    /// <summary>The DHT namespace IPNS values live under.</summary>
    public const string Namespace = "/ipns/";

    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    public required string Name { get; init; } // PeerID of the IPNS key
    public required string Value { get; init; } // Manifest CID (bafyrei…)
    public required long Sequence { get; init; }
    public required DateTimeOffset Validity { get; init; } // Expiry (EOL)
    public required string ValidityType { get; init; } = ValidityTypeEol;
    public required long TtlSeconds { get; init; } = DefaultTtlSeconds;
    public required byte[] PublicKey { get; init; } // 32-byte Ed25519 public key
    public byte[]? Signature { get; set; } // 64-byte Ed25519, null until signed

    /// <summary>Canonical dag-cbor of the record map WITHOUT the signature — the signature input.</summary>
    public byte[] SignatureInput() => DagCbor.Encode(HeaderMap());

    /// <summary>Signs with the IPNS key. The key's PeerID must equal <see cref="Name"/>.</summary>
    public void Sign(Identity signingKey)
    {
        if (signingKey.PeerId.ToString() != Name)
        {
            throw new InvalidOperationException(
                $"IPNS signing key {signingKey.PeerId} does not match record name {Name}");
        }

        Signature = signingKey.Sign(SignatureInput());
    }

    /// <summary>Canonical dag-cbor of the full record (including signature). Throws if unsigned.</summary>
    public byte[] Encode()
    {
        if (Signature is null) throw new InvalidOperationException("Cannot encode an unsigned IPNS record");
        return DagCbor.Encode(FullMap());
    }

    /// <summary>
    ///     Structural + cryptographic verification of the record on its own (no held state).
    ///     Checks: public key is 32 bytes, its PeerID equals the name, the signature
    ///     verifies over the canonical header, the value parses as a CID, the
    ///     validity type is "eol", the TTL is positive, the sequence is &gt;= 1,
    ///     and the validity window has not expired at <paramref name="now"/>.
    ///     Sequence-vs-held is the resolver's job (<see cref="IpnsResolver"/>), not this method's.
    /// </summary>
    public (bool Ok, string Reason) Verify(DateTimeOffset now)
    {
        if (PublicKey.Length != 32) return (false, "IPNS public key is not 32 bytes");
        string derived;
        try
        {
            derived = new PeerId(new PublicKey
            {
                Type = KeyType.Ed25519,
                Data = ByteString.CopyFrom(PublicKey),
            }).ToString();
        }
        catch (Exception ex) { return (false, $"IPNS public key does not derive a PeerID: {ex.Message}"); }
        if (derived != Name) return (false, $"IPNS name {Name} does not match its public key (derives {derived})");
        if (Signature is null) return (false, "IPNS record is unsigned");
        try
        {
            var verifier = new Identity(new PublicKey
            {
                Type = KeyType.Ed25519,
                Data = ByteString.CopyFrom(PublicKey),
            });
            if (!verifier.VerifySignature(SignatureInput(), Signature))
                return (false, "IPNS signature does not verify");
        }
        catch (Exception ex) { return (false, $"IPNS signature check failed: {ex.Message}"); }
        try { _ = BitswapCid.Parse(Value); }
        catch (Exception ex) { return (false, $"IPNS value is not a CID: {ex.Message}"); }
        if (ValidityType != ValidityTypeEol) return (false, $"unknown IPNS validity_type '{ValidityType}'");
        if (TtlSeconds <= 0) return (false, $"IPNS ttl_seconds must be positive, got {TtlSeconds}");
        if (Sequence < FirstSequence) return (false, $"IPNS sequence must be >= {FirstSequence}, got {Sequence}");
        if (now >= Validity) return (false, $"IPNS record expired at {FormatTimestamp(Validity)} (now {FormatTimestamp(now)})");
        return (true, "ok");
    }

    /// <summary>The DHT key for this name: ASCII "/ipns/" + the binary PeerID (multihash bytes).</summary>
    public static byte[] DhtKey(string name)
    {
        byte[] peerIdBytes = new PeerId(name).Bytes;
        byte[] prefix = System.Text.Encoding.ASCII.GetBytes(Namespace);
        return [.. prefix, .. peerIdBytes];
    }

    public static IpnsRecord Decode(byte[] bytes)
    {
        if (DagCbor.Decode(bytes) is not DagCbor.CborMap map)
            throw new InvalidDataException("IPNS record must be a CBOR map");
        var fields = new Dictionary<string, DagCbor.CborValue>(StringComparer.Ordinal);
        foreach (var kv in map.Entries)
        {
            if (kv.Key is not DagCbor.CborText t) throw new InvalidDataException("IPNS record map keys must be text");
            fields[t.Value] = kv.Value;
        }
        string Text(string key) => fields.TryGetValue(key, out var v) && v is DagCbor.CborText t ? t.Value
            : throw new InvalidDataException($"IPNS record field '{key}' missing or not text");
        long Int(string key) => fields.TryGetValue(key, out var v) && v is DagCbor.CborInt i ? i.Value
            : throw new InvalidDataException($"IPNS record field '{key}' missing or not an integer");
        byte[] Bytes(string key) => fields.TryGetValue(key, out var v) && v is DagCbor.CborBytes b ? b.Value
            : throw new InvalidDataException($"IPNS record field '{key}' missing or not bytes");
        return new IpnsRecord
        {
            Name = Text("name"),
            Value = Text("value"),
            Sequence = Int("sequence"),
            Validity = DateTimeOffset.ParseExact(Text("validity"), TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal),
            ValidityType = Text("validity_type"),
            TtlSeconds = Int("ttl_seconds"),
            PublicKey = Bytes("public_key"),
            Signature = Bytes("signature"),
        };
    }

    internal static string FormatTimestamp(DateTimeOffset t) =>
        t.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private DagCbor.CborMap HeaderMap()
    {
        var entries = new List<KeyValuePair<DagCbor.CborValue, DagCbor.CborValue>>
        {
            KV("name", new DagCbor.CborText(Name)),
            KV("value", new DagCbor.CborText(Value)),
            KV("sequence", new DagCbor.CborInt(Sequence)),
            KV("validity", new DagCbor.CborText(FormatTimestamp(Validity))),
            KV("validity_type", new DagCbor.CborText(ValidityType)),
            KV("ttl_seconds", new DagCbor.CborInt(TtlSeconds)),
            KV("public_key", new DagCbor.CborBytes(PublicKey)),
        };
        return new DagCbor.CborMap(entries);
    }

    private DagCbor.CborMap FullMap()
    {
        var entries = HeaderMap().Entries.ToList();
        entries.Add(KV("signature", new DagCbor.CborBytes(Signature!)));
        return new DagCbor.CborMap(entries);
    }

    private static KeyValuePair<DagCbor.CborValue, DagCbor.CborValue> KV(string key, DagCbor.CborValue value) =>
        new(new DagCbor.CborText(key), value);
}
