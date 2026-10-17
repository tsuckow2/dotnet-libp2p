// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Ipfs.Bitswap;
using ProtoBuf;

namespace Nethermind.Ipfs.UnixFs;

/// <summary>
///     Reading files out of the DAG: given a way to fetch blocks by
///     CID, reassemble a UnixFS file — inline content for small
///     files, link-ordered chunks (raw leaves or chunk nodes) for
///     split ones, recursively.
/// </summary>
public static class UnixFsFile
{
    public delegate Task<byte[]> GetBlock(Cid cid, CancellationToken token);

    /// <summary>Reads the whole file rooted at the given CID.</summary>
    public static async Task<byte[]> ReadAsync(Cid root, GetBlock getBlock, CancellationToken token = default)
    {
        byte[] block = await getBlock(root, token);
        if (root.Codec == Cid.RawCodec)
        {
            return block;
        }

        if (root.Codec != Cid.DagPbCodec)
        {
            throw new NotSupportedException($"A file root with codec 0x{root.Codec:x} is not a UnixFS node.");
        }

        PBNode node = Serializer.Deserialize<PBNode>(new MemoryStream(block));
        UnixFsData data = Serializer.Deserialize<UnixFsData>(new MemoryStream(node.Data));
        if (data.Type != UnixFsDataType.File)
        {
            throw new InvalidDataException($"The root is a {data.Type}, not a file.");
        }

        byte[] content = await ReadNodeContentAsync(node, data, getBlock, token);
        if (data.FileSize != 0 && content.Length != (long)data.FileSize)
        {
            throw new InvalidDataException(
                $"Reassembled {content.Length} bytes, but the file declares {data.FileSize}.");
        }

        return content;
    }

    /// <summary>Lists a directory root's entries: link name → child CID.</summary>
    public static async Task<IReadOnlyList<(string Name, Cid Cid)>> ListDirectoryAsync(
        Cid root, GetBlock getBlock, CancellationToken token = default)
    {
        byte[] block = await getBlock(root, token);
        PBNode node = Serializer.Deserialize<PBNode>(new MemoryStream(block));
        UnixFsData data = Serializer.Deserialize<UnixFsData>(new MemoryStream(node.Data));
        if (data.Type != UnixFsDataType.Directory)
        {
            throw new InvalidDataException($"The root is a {data.Type}, not a directory.");
        }

        return (node.Links ?? [])
            .Select(link => (link.Name, Cid.FromBytes(link.Hash)))
            .ToList();
    }

    private static async Task<byte[]> ReadNodeContentAsync(
        PBNode node, UnixFsData data, GetBlock getBlock, CancellationToken token)
    {
        if (node.Links is null || node.Links.Count == 0)
        {
            return data.Data ?? [];
        }

        using var content = new MemoryStream();
        foreach (PBLink link in node.Links)
        {
            Cid child = Cid.FromBytes(link.Hash);
            byte[] childBlock = await getBlock(child, token);
            if (child.Codec == Cid.RawCodec)
            {
                await content.WriteAsync(childBlock, token);
                continue;
            }

            // A chunk node: its own content, recursively (a chunk can
            // itself be split when the DAG is deep).
            PBNode childNode = Serializer.Deserialize<PBNode>(new MemoryStream(childBlock));
            UnixFsData childData = Serializer.Deserialize<UnixFsData>(new MemoryStream(childNode.Data));
            byte[] chunk = await ReadNodeContentAsync(childNode, childData, getBlock, token);
            await content.WriteAsync(chunk, token);
        }

        return content.ToArray();
    }
}
