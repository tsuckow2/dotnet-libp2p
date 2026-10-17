// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Ipfs.Bitswap.Tests;

/// <summary>CID parsing and printing against known vectors.</summary>
[TestFixture]
public class CidTests
{
    [Test]
    public void Parses_V1_DagPb()
    {
        // The IPFS documentation's example directory.
        Cid cid = Cid.Parse("bafybeigdyrzt5sfp7udm7hu76uh7y26nf3efuylqabf3oclgtqy55fbzdi");

        Assert.That(cid.Version, Is.EqualTo(1));
        Assert.That(cid.Codec, Is.EqualTo(Cid.DagPbCodec));
        Assert.That(Convert.ToHexString(cid.Multihash),
            Is.EqualTo("1220C3C4733EC8AFFD06CF9E9FF50FFC6BCD2EC85A6170004BB709669C31DE94391A"));
        Assert.That(cid.ToString(), Is.EqualTo("bafybeigdyrzt5sfp7udm7hu76uh7y26nf3efuylqabf3oclgtqy55fbzdi"));
    }

    [Test]
    public void Parses_V1_Raw_EmptyFile()
    {
        // The well-known CID of the empty file (raw codec).
        Cid cid = Cid.Parse("bafkreihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku");

        Assert.That(cid.Codec, Is.EqualTo(Cid.RawCodec));
        Assert.That(Convert.ToHexString(cid.Digest),
            Is.EqualTo("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"));
    }

    [Test]
    public void Parses_V0()
    {
        // The canonical empty-directory CIDv0.
        Cid cid = Cid.Parse("QmUNLLsPACCz1vLxQVkXqqLX5R1X345qqfHbsf67hvA3Nn");

        Assert.That(cid.Version, Is.EqualTo(0));
        Assert.That(cid.Codec, Is.EqualTo(Cid.DagPbCodec));
        Assert.That(cid.Bytes, Is.EqualTo(cid.Multihash));
        Assert.That(cid.Digest, Has.Length.EqualTo(32));
        Assert.That(cid.ToString(), Is.EqualTo("QmUNLLsPACCz1vLxQVkXqqLX5R1X345qqfHbsf67hvA3Nn"));
    }

    [Test]
    public void Creates_FromContent()
    {
        Cid cid = Cid.Create(Cid.RawCodec, "hello"u8.ToArray());

        Assert.That(cid.Version, Is.EqualTo(1));
        Assert.That(Convert.ToHexString(cid.Digest),
            Is.EqualTo("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824"));
        Assert.That(cid.Prefix, Has.Length.EqualTo(cid.Bytes.Length - 32));
        Assert.That(Cid.Parse(cid.ToString()), Is.EqualTo(cid));
    }

    [Test]
    public void FromBytes_RoundTrips_WireForm()
    {
        Cid cid = Cid.Parse("bafybeigdyrzt5sfp7udm7hu76uh7y26nf3efuylqabf3oclgtqy55fbzdi");
        Assert.That(Cid.FromBytes(cid.Bytes), Is.EqualTo(cid));
    }
}
