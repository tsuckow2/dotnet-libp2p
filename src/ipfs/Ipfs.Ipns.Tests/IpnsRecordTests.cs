// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Nethermind.Ipfs.Ipns.Tests;

/// <summary>
///     The IPNS record, against the golden vector the backup
///     prototype's own encoder produced (fixed seed 0x01..0x20,
///     manifest CID of fixed bytes, sequence 1, validity
///     2026-11-22T12:00:00Z): a record this library builds from the
///     same inputs must be byte-identical to the prototype's, and a
///     record the prototype built must verify here. That is the
///     compatibility promise the backup POC switches transports on.
/// </summary>
[TestFixture]
public class IpnsRecordTests
{
    // The prototype's ipns-vector run (seed 1..32):
    private const string Name = "12D3KooWJ1TsijH7H5F74hfAD5XishQz3sxrmAtVY37GtNd9CqYf";
    private const string Value = "bafyreigqcwstshwy4qff3zot3tjtdirbtt72n6x3r6rjcfon7odxdsmy2a";
    private const string DhtKeyHex = "2f69706e732f00240801122079b5562e8fe654f94078b112e8a98ba7901f853ae695bed7e0e3910bad049664";
    private const string EncodedHex = "a8646e616d657834313244334b6f6f574a315473696a48374835463734686641443558697368517a337378726d41745659333747744e6439437159666576616c7565783b626166797265696771637773747368777934716666337a6f7433746a7464697262747437326e3678337236726a63666f6e376f647864736d7932616873657175656e6365016876616c696469747974323032362d31312d32325431323a30303a30305a697369676e61747572655840a2c020391be4c553b0ce53ba61a8b7dc52af1c6ee3193138a623b15ef9d778c2517db32fcf8c6e50d4a69f1db8d28f4da479f7e046c11b3afe3eabcecb78a0076a7075626c69635f6b6579582079b5562e8fe654f94078b112e8a98ba7901f853ae695bed7e0e3910bad0496646b74746c5f7365636f6e6473190e106d76616c69646974795f7479706563656f6c";
    private const string SignatureInputHex = "a7646e616d657834313244334b6f6f574a315473696a48374835463734686641443558697368517a337378726d41745659333747744e6439437159666576616c7565783b626166797265696771637773747368777934716666337a6f7433746a7464697262747437326e3678337236726a63666f6e376f647864736d7932616873657175656e6365016876616c696469747974323032362d31312d32325431323a30303a30305a6a7075626c69635f6b6579582079b5562e8fe654f94078b112e8a98ba7901f853ae695bed7e0e3910bad0496646b74746c5f7365636f6e6473190e106d76616c69646974795f7479706563656f6c";

    private static readonly DateTimeOffset BeforeExpiry = new(2026, 11, 22, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterExpiry = new(2026, 11, 22, 12, 0, 1, TimeSpan.Zero);

    private static Identity GoldenKey()
    {
        byte[] seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        return new Identity(seed);
    }

    private static IpnsRecord GoldenRecord(Identity key) => new()
    {
        Name = Name,
        Value = Value,
        Sequence = 1,
        Validity = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero),
        ValidityType = IpnsRecord.ValidityTypeEol,
        TtlSeconds = IpnsRecord.DefaultTtlSeconds,
        PublicKey = key.PublicKey.Data.ToByteArray(),
    };

    [Test]
    public void GoldenVector_EncodeIsByteIdenticalToThePrototype()
    {
        Identity key = GoldenKey();
        Assert.That(key.PeerId.ToString(), Is.EqualTo(Name));

        IpnsRecord record = GoldenRecord(key);
        Assert.That(record.SignatureInput(), Is.EqualTo(Convert.FromHexString(SignatureInputHex)));
        record.Sign(key);
        Assert.That(record.Encode(), Is.EqualTo(Convert.FromHexString(EncodedHex)));
        Assert.That(IpnsRecord.DhtKey(Name), Is.EqualTo(Convert.FromHexString(DhtKeyHex)));
    }

    [Test]
    public void GoldenVector_PrototypeBytesDecodeAndVerifyHere()
    {
        IpnsRecord decoded = IpnsRecord.Decode(Convert.FromHexString(EncodedHex));
        Assert.That(decoded.Name, Is.EqualTo(Name));
        Assert.That(decoded.Value, Is.EqualTo(Value));
        Assert.That(decoded.Sequence, Is.EqualTo(1));
        Assert.That(decoded.Validity, Is.EqualTo(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero)));
        Assert.That(decoded.Verify(BeforeExpiry), Is.EqualTo((true, "ok")));
        Assert.That(decoded.Encode(), Is.EqualTo(Convert.FromHexString(EncodedHex)));
    }

    [Test]
    public void SignAndVerify_RoundTrips()
    {
        Identity key = new();
        var record = new IpnsRecord
        {
            Name = key.PeerId.ToString(),
            Value = Value,
            Sequence = 7,
            Validity = DateTimeOffset.UtcNow.AddHours(24),
            ValidityType = IpnsRecord.ValidityTypeEol,
            TtlSeconds = IpnsRecord.DefaultTtlSeconds,
            PublicKey = key.PublicKey.Data.ToByteArray(),
        };
        record.Sign(key);
        IpnsRecord decoded = IpnsRecord.Decode(record.Encode());
        Assert.That(decoded.Verify(DateTimeOffset.UtcNow).Ok, Is.True);
        Assert.That(decoded.Sequence, Is.EqualTo(7));
    }

    [Test]
    public void Sign_WithTheWrongKey_Throws()
    {
        IpnsRecord record = GoldenRecord(GoldenKey());
        Assert.Throws<InvalidOperationException>(() => record.Sign(new Identity()));
    }

    [Test]
    public void Verify_TamperedValue_Fails()
    {
        Identity key = GoldenKey();
        IpnsRecord record = GoldenRecord(key);
        record.Sign(key);
        IpnsRecord tampered = record with { Value = "bafyreigqcwstshwy4qff3zot3tjtdirbtt72n6x3r6rjcfon7odxdsmy2b" };
        Assert.That(tampered.Verify(BeforeExpiry).Ok, Is.False);
    }

    [Test]
    public void Verify_ExpiredRecord_Fails()
    {
        IpnsRecord decoded = IpnsRecord.Decode(Convert.FromHexString(EncodedHex));
        Assert.That(decoded.Verify(AfterExpiry).Ok, Is.False);
    }

    [Test]
    public void Verify_ValueThatIsNotACid_Fails()
    {
        Identity key = new();
        var record = new IpnsRecord
        {
            Name = key.PeerId.ToString(),
            Value = "not-a-cid",
            Sequence = 1,
            Validity = DateTimeOffset.UtcNow.AddHours(24),
            ValidityType = IpnsRecord.ValidityTypeEol,
            TtlSeconds = IpnsRecord.DefaultTtlSeconds,
            PublicKey = key.PublicKey.Data.ToByteArray(),
        };
        record.Sign(key);
        Assert.That(record.Verify(DateTimeOffset.UtcNow).Ok, Is.False);
    }

    [Test]
    public void Decode_TrailingBytes_Throws()
    {
        byte[] bytes = [.. Convert.FromHexString(EncodedHex), 0x00];
        Assert.Throws<InvalidDataException>(() => IpnsRecord.Decode(bytes));
    }

    [Test]
    public void Resolver_AcceptsNewer_DuplicatesAndRejectsStale()
    {
        Identity key = GoldenKey();
        var resolver = new IpnsResolver();
        IpnsRecord first = GoldenRecord(key);
        first.Sign(key);
        Assert.That(resolver.TryAccept(first, BeforeExpiry).Outcome, Is.EqualTo(IpnsAcceptOutcome.Accepted));

        // Same record again: a duplicate, not an error.
        Assert.That(resolver.TryAccept(first, BeforeExpiry).Outcome, Is.EqualTo(IpnsAcceptOutcome.Duplicate));

        // Same sequence, different value: a conflict; the held record wins.
        IpnsRecord conflict = first with { Value = "bafyreigqcwstshwy4qff3zot3tjtdirbtt72n6x3r6rjcfon7odxdsmy2b" };
        conflict.Sign(key);
        Assert.That(resolver.TryAccept(conflict, BeforeExpiry).Outcome, Is.EqualTo(IpnsAcceptOutcome.Stale));
        Assert.That(resolver.Get(Name)!.Value.Record.Value, Is.EqualTo(Value));

        // Newer sequence: accepted.
        IpnsRecord second = first with { Sequence = 2, Value = "bafyreigqcwstshwy4qff3zot3tjtdirbtt72n6x3r6rjcfon7odxdsmy2b" };
        second.Sign(key);
        Assert.That(resolver.TryAccept(second, BeforeExpiry).Outcome, Is.EqualTo(IpnsAcceptOutcome.Accepted));
        Assert.That(resolver.Get(Name)!.Value.Record.Sequence, Is.EqualTo(2));
    }
}
