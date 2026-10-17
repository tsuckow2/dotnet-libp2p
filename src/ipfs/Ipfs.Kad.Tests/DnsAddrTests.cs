// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Ipfs.Kad.Tests;

/// <summary>/dnsaddr/ machinery: host extraction and TXT packet parsing against a hand-built response.</summary>
[TestFixture]
public class DnsAddrTests
{
    [Test]
    public void Extracts_DnsAddr_Host()
    {
        Assert.That(DnsAddr.DnsAddrHost("/dnsaddr/bootstrap.libp2p.io/p2p/QmNnooDu7bfjPFoTZYxMNLWUQJyrVwtbZg5gBMjTezGAJN"),
            Is.EqualTo("bootstrap.libp2p.io"));
        Assert.That(DnsAddr.DnsAddrHost("/ip4/104.131.131.82/tcp/4001"), Is.Null);
    }

    [Test]
    public void Parses_Txt_Records()
    {
        // A response carrying two TXT answers (the shape
        // _dnsaddr.bootstrap.libp2p.io returns), one of them split
        // across two character-strings.
        byte[] packet = BuildResponse(
            "dnsaddr=/ip4/147.75.80.110/tcp/4001/p2p/QmNnooDu7bfjPFoTZYxMNLWUQJyrVwtbZg5gBMjTezGAJN",
            "dnsaddr=/ip4/147.75.83.83/tcp/4001/p2p/QmQCU2EcMqAqQPR2i9bChDtGNJchTbq5TbXJJ16u19uLTa");

        IReadOnlyList<string> records = DnsAddr.ParseTxtRecords(packet);

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[0], Does.StartWith("dnsaddr=/ip4/147.75.80.110"));
        Assert.That(records[1], Does.Contain("QmQCU2EcMqAqQPR2i9bChDtGNJchTbq5TbXJJ16u19uLTa"));
    }

    private static byte[] BuildResponse(params string[] txtRecords)
    {
        using var s = new MemoryStream();
        void U16(int v)
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        U16(0x2a41); U16(0x8180); U16(1); U16(txtRecords.Length); U16(0); U16(0);

        // Question: _dnsaddr.bootstrap.libp2p.io TXT IN
        foreach (string label in new[] { "_dnsaddr", "bootstrap", "libp2p", "io" })
        {
            s.WriteByte((byte)label.Length);
            s.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }

        s.WriteByte(0);
        U16(16); U16(1);

        // Answers: name as a compression pointer to the question.
        bool first = true;
        foreach (string record in txtRecords)
        {
            s.WriteByte(0xC0); s.WriteByte(0x0C);
            U16(16); U16(1); U16(0); U16(60); // type, class, ttl (4 bytes: two U16)
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(record);
            if (first && bytes.Length > 255)
            {
                throw new InvalidOperationException("Fixture record too long.");
            }

            // One record split in two strings, the rest single.
            if (!first)
            {
                int half = bytes.Length / 2;
                U16(half + 1 + (bytes.Length - half) + 1);
                s.WriteByte((byte)half); s.Write(bytes, 0, half);
                s.WriteByte((byte)(bytes.Length - half)); s.Write(bytes, half, bytes.Length - half);
            }
            else
            {
                U16(bytes.Length + 1);
                s.WriteByte((byte)bytes.Length); s.Write(bytes);
            }

            first = false;
        }

        return s.ToArray();
    }
}
