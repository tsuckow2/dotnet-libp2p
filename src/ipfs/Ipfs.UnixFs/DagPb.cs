// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using ProtoBuf;

namespace Nethermind.Ipfs.UnixFs;

/// <summary>
///     The dag-pb wire format (https://ipld.io/specs/codecs/dag-pb/):
///     a node is a bag of links plus an opaque data blob, which for
///     UnixFS is the <see cref="UnixFsData"/> message below.
/// </summary>
[ProtoContract]
public class PBNode
{
    [ProtoMember(1)]
    public byte[] Data;

    [ProtoMember(2)]
    public List<PBLink> Links;
}

[ProtoContract]
public class PBLink
{
    /// <summary>The linked block's id: a full CID, or a bare multihash for CIDv0 links.</summary>
    [ProtoMember(1)]
    public byte[] Hash;

    [ProtoMember(2)]
    public string Name;

    /// <summary>The cumulative size of the linked DAG.</summary>
    [ProtoMember(3)]
    public ulong Tsize;
}

public enum UnixFsDataType
{
    Raw = 0,
    Directory = 1,
    File = 2,
    Metadata = 3,
    Symlink = 4,
    HamtShard = 5,
}

/// <summary>The UnixFS message carried in a PBNode's Data.</summary>
[ProtoContract]
public class UnixFsData
{
    [ProtoMember(1, IsRequired = true)]
    public UnixFsDataType Type;

    /// <summary>Inline content (small files, and each file chunk's node).</summary>
    [ProtoMember(2)]
    public byte[] Data;

    [ProtoMember(3)]
    public ulong FileSize;

    /// <summary>The size of each chunk, in link order, for files split over links.</summary>
    [ProtoMember(4)]
    public List<ulong> BlockSizes;
}
