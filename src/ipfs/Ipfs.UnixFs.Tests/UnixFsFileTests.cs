// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;
using Nethermind.Ipfs.Bitswap;

namespace Nethermind.Ipfs.UnixFs.Tests;

/// <summary>
///     UnixFS reassembly against fixtures encoded with a hand-rolled
///     protobuf writer, so the DTO field numbers are tested against
///     the wire, not against the same serializer that wrote them.
/// </summary>
[TestFixture]
public class UnixFsFileTests
{
    private readonly Dictionary<string, byte[]> _blocks = new();

    private UnixFsFile.GetBlock Getter => (cid, _) =>
        Task.FromResult(_blocks[Convert.ToHexString(cid.Bytes)]);

    private Cid Put(ulong codec, byte[] block)
    {
        Cid cid = Cid.Create(codec, block);
        _blocks[Convert.ToHexString(cid.Bytes)] = block;
        return cid;
    }

    [Test]
    public async Task Reads_InlineFile()
    {
        byte[] data = UnixFs(fileSize: 5, inline: "hello"u8.ToArray());
        Cid root = Put(Cid.DagPbCodec, PbNode(data));

        byte[] content = await UnixFsFile.ReadAsync(root, Getter);

        Assert.That(Encoding.UTF8.GetString(content), Is.EqualTo("hello"));
    }

    [Test]
    public async Task Reads_FileOverRawLeaves()
    {
        Cid leaf1 = Put(Cid.RawCodec, "hello "u8.ToArray());
        Cid leaf2 = Put(Cid.RawCodec, "world"u8.ToArray());
        byte[] data = UnixFs(fileSize: 11, blockSizes: [6, 5]);
        Cid root = Put(Cid.DagPbCodec, PbNode(data,
            Link(leaf1, "a", 6), Link(leaf2, "b", 5)));

        byte[] content = await UnixFsFile.ReadAsync(root, Getter);

        Assert.That(Encoding.UTF8.GetString(content), Is.EqualTo("hello world"));
    }

    [Test]
    public async Task Reads_FileOverChunkNodes()
    {
        byte[] chunkData = UnixFs(fileSize: 5, inline: "chunk"u8.ToArray());
        Cid chunk = Put(Cid.DagPbCodec, PbNode(chunkData));
        byte[] data = UnixFs(fileSize: 5, blockSizes: [5]);
        Cid root = Put(Cid.DagPbCodec, PbNode(data, Link(chunk, "", 100)));

        byte[] content = await UnixFsFile.ReadAsync(root, Getter);

        Assert.That(Encoding.UTF8.GetString(content), Is.EqualTo("chunk"));
    }

    [Test]
    public async Task Lists_Directory()
    {
        Cid child = Put(Cid.RawCodec, "x"u8.ToArray());
        byte[] dirData = UnixFs(type: 1);
        Cid root = Put(Cid.DagPbCodec, PbNode(dirData, Link(child, "file.txt", 1)));

        IReadOnlyList<(string Name, Cid Cid)> entries = await UnixFsFile.ListDirectoryAsync(root, Getter);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Name, Is.EqualTo("file.txt"));
        Assert.That(entries[0].Cid, Is.EqualTo(child));
    }

    [Test]
    public void ReadAsync_Rejects_Directory()
    {
        Cid root = Put(Cid.DagPbCodec, PbNode(UnixFs(type: 1)));

        Assert.ThrowsAsync<InvalidDataException>(async () => await UnixFsFile.ReadAsync(root, Getter));
    }

    // ---- hand-rolled protobuf -------------------------------------

    private static byte[] UnixFs(int type = 2, ulong fileSize = 0, byte[]? inline = null, ulong[]? blockSizes = null)
    {
        using var s = new MemoryStream();
        Field(s, 1, 0); Varint(s, (ulong)type);
        if (inline != null)
        {
            Field(s, 2, 2); Bytes(s, inline);
        }

        if (fileSize != 0)
        {
            Field(s, 3, 0); Varint(s, fileSize);
        }

        foreach (ulong size in blockSizes ?? [])
        {
            Field(s, 4, 0); Varint(s, size);
        }

        return s.ToArray();
    }

    private static (byte[] Hash, string Name, ulong Tsize) Link(Cid cid, string name, ulong tsize)
        => (cid.Bytes, name, tsize);

    private static byte[] PbNode(byte[] data, params (byte[] Hash, string Name, ulong Tsize)[] links)
    {
        using var s = new MemoryStream();
        Field(s, 1, 2); Bytes(s, data);
        foreach ((byte[] hash, string name, ulong tsize) in links)
        {
            using var linkStream = new MemoryStream();
            Field(linkStream, 1, 2); Bytes(linkStream, hash);
            Field(linkStream, 2, 2); Bytes(linkStream, Encoding.UTF8.GetBytes(name));
            Field(linkStream, 3, 0); Varint(linkStream, tsize);
            Field(s, 2, 2); Bytes(s, linkStream.ToArray());
        }

        return s.ToArray();
    }

    private static void Field(Stream s, int number, int wireType) => Varint(s, (ulong)(number << 3 | wireType));

    private static void Bytes(Stream s, byte[] bytes)
    {
        Varint(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    private static void Varint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        s.WriteByte((byte)value);
    }
}
