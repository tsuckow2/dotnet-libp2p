// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using ProtoBuf;

/// <summary>
///     The typed comm Bitswap hands back: the channel plus the wants and
///     blocks received over it. The block-exchange engine (wantlist
///     management, block store) hooks onto these events; the protocol
///     itself only speaks wire format.
/// </summary>
class BitSwapProtocolChannel
{
    public IChannel Channel { get; }

    /// <summary>Raised for each wantlist entry: (block cid bytes, is cancel).</summary>
    public event Action<byte[], bool>? OnWant;

    /// <summary>Raised for each received block: (cid prefix bytes, data).</summary>
    public event Action<byte[], byte[]>? OnBlock;

    public BitSwapProtocolChannel(IChannel channel)
    {
        Channel = channel;
    }

    internal void RaiseWant(byte[] block, bool cancel) => OnWant?.Invoke(block, cancel);
    internal void RaiseBlock(byte[] prefix, byte[] data) => OnBlock?.Invoke(prefix, data);
}

/// <summary>
///     Bitswap 1.1.0 (https://github.com/ipfs/specs/blob/main/BITSWAP.md),
///     as a managed-tier protocol: negotiate, then exchange protobuf
///     Messages of wantlists and block payloads.
/// </summary>
internal class BitSwap11Protocol : SymmetricProtocol<BitSwapProtocolChannel>
{
    public static ProtocolRef<BitSwap11Protocol, BitSwapProtocolChannel> Ref => new();

    public override string Id => "/ipfs/bitswap/1.1.0";

    protected override async Task NegotiateAsync(IManagedRemoteComm comm)
    {
        await NegotiationUtils.NegotiateProtocolAsync(comm, Id);
    }

    protected override async Task<IManagedProtocolComm<BitSwapProtocolChannel>> ConnectAsync(IManagedRemoteComm comm, bool isListener)
    {
        var protocolChannel = new BitSwapProtocolChannel(comm.Channel);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Message request = await ReadProtobufAsync<Message>(comm.Channel);

                // Process want list
                if (request.wantlist?.entries != null)
                {
                    foreach (Entry entry in request.wantlist.entries)
                    {
                        protocolChannel.RaiseWant(entry.block, entry.cancel);
                    }
                }

                // Forward received blocks to the engine
                if (request.payload != null)
                {
                    foreach (Block sentBlock in request.payload)
                    {
                        protocolChannel.RaiseBlock(sentBlock.prefix, sentBlock.data);
                    }
                }
            }
        });

        return new ManagedProtocolComm<BitSwapProtocolChannel>(protocolChannel, comm);
    }

    public static async Task<T> ReadProtobufAsync<T>(IChannel channel)
    {
        var length = await channel.ReadVarintAsync().ConfigureAwait(false);
        var result = await channel.ReadAsync(length, ReadBlockingMode.WaitAll).ConfigureAwait(false);
        if (result.Result != IOResult.Ok)
        {
            throw new InvalidOperationException("Failed to read data from the channel.");
        }

        return ProtoBuf.Serializer.Deserialize<T>(result.Data);
    }

    [ProtoContract]
    class Entry
    {
        [ProtoMember(1)]
        // changed from string to bytes, it makes a difference in JavaScript
        public byte[] block;      // the block cid (cidV0 in bitswap 1.0.0, cidV1 in bitswap 1.1.0)

        [ProtoMember(2)]
        public int priority = 1;    // the priority (normalized). default to 1

        [ProtoMember(3)]
        public bool cancel;       // whether this revokes an entry
    }

    [ProtoContract]
    class Wantlist
    {
        [ProtoMember(1)]
        public Entry[] entries;       // a list of wantlist entries

        [ProtoMember(2)]
        public bool full;           // whether this is the full wantlist. default to false
    }

    [ProtoContract]
    class Block
    {
        [ProtoMember(1)]
        public byte[] prefix;        // CID prefix (cid version, multicodec and multihash prefix (type + length)

        [ProtoMember(2)]
        public byte[] data;
    }

    [ProtoContract]
    class Message
    {
        [ProtoMember(1)]
        public Wantlist wantlist;

        [ProtoMember(2)]
        public byte[][] blocks;          // used to send Blocks in bitswap 1.0.0

        [ProtoMember(3)]
        public List<Block> payload;         // used to send Blocks in bitswap 1.1.0
    }
}
