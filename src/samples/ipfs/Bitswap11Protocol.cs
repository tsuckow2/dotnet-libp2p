// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Nethermind.Libp2p.Core;
using ProtoBuf;

/// <summary>
///     Block ids for the sample: a block is identified by the multihash
///     (sha2-256) of its content. That keeps ids self-verifying without a
///     CID/UnixFS layer — deliberately narrower than real IPFS, where the
///     wantlist carries full CIDs and files are UnixFS DAGs.
/// </summary>
static class BlockIds
{
    public const int MultihashLength = 34; // 0x12 0x20 + 32-byte digest

    public static byte[] IdFor(byte[] data)
    {
        byte[] digest = SHA256.HashData(data);
        byte[] id = new byte[MultihashLength];
        id[0] = 0x12; // sha2-256
        id[1] = 0x20; // 32 bytes
        digest.CopyTo(id, 2);
        return id;
    }

    /// <summary>The id a received block actually has, from its prefix and content.</summary>
    public static byte[] IdForReceived(byte[] prefix, byte[] data)
    {
        // The sender puts the multihash code+length in the block prefix;
        // the digest part of the id is recomputed from the content, so a
        // corrupted or substituted block never matches the wanted id.
        byte[] digest = SHA256.HashData(data);
        byte[] id = new byte[prefix.Length + digest.Length];
        prefix.CopyTo(id, 0);
        digest.CopyTo(id, prefix.Length);
        return id;
    }

    public static string Key(byte[] id) => Convert.ToHexString(id);
    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}

/// <summary>Where blocks live. The sample keeps them in memory.</summary>
interface IBlockStore
{
    Task<byte[]?> GetAsync(byte[] blockId);
    Task PutAsync(byte[] blockId, byte[] data);
}

class InMemoryBlockStore : IBlockStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blocks = new();

    public Task<byte[]?> GetAsync(byte[] blockId)
    {
        return Task.FromResult(_blocks.TryGetValue(BlockIds.Key(blockId), out byte[]? data) ? data : null);
    }

    public Task PutAsync(byte[] blockId, byte[] data)
    {
        _blocks[BlockIds.Key(blockId)] = data;
        return Task.CompletedTask;
    }
}

/// <summary>
///     A live Bitswap session: the typed comm the protocol hands back.
///     It owns the channel's read loop, answers wants from the block
///     store, and matches received blocks to outstanding wants — a block
///     only completes a want when the id recomputed from its content
///     matches, so verification is inherent, not a separate step.
/// </summary>
class BitswapSession
{
    public const int ChunkSize = 256 * 1024;

    private readonly IChannel _channel;
    private readonly IBlockStore _store;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public IChannel Channel => _channel;

    /// <summary>Raised for each wantlist entry received: (block id, is cancel).</summary>
    public event Action<byte[], bool>? OnWant;

    /// <summary>Raised for each block received: (block id, data).</summary>
    public event Action<byte[], byte[]>? OnBlock;

    public BitswapSession(IChannel channel, IBlockStore store)
    {
        _channel = channel;
        _store = store;
        _ = Task.Run(ReceiveLoopAsync);
    }

    // ---- sending --------------------------------------------------------

    private async Task SendAsync(Message message)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, message);
        byte[] payload = stream.ToArray();

        await _sendLock.WaitAsync();
        try
        {
            await _channel.WriteVarintAsync(payload.Length);
            await _channel.WriteAsync(new ReadOnlySequence<byte>(payload));
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendBlockAsync(byte[] blockId, byte[] data)
    {
        await SendAsync(new Message
        {
            payload = [new Block { prefix = blockId[..2], data = data }],
        });
    }

    // ---- receiving ------------------------------------------------------

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                Message message = await BitswapWire.ReadMessageAsync(_channel);

                if (message.wantlist?.entries != null)
                {
                    foreach (Entry entry in message.wantlist.entries)
                    {
                        OnWant?.Invoke(entry.block, entry.cancel);
                        if (entry.cancel)
                        {
                            continue;
                        }
                        byte[]? data = await _store.GetAsync(entry.block);
                        if (data != null)
                        {
                            await SendBlockAsync(entry.block, data);
                        }
                    }
                }

                if (message.payload != null)
                {
                    foreach (Block block in message.payload)
                    {
                        byte[] id = BlockIds.IdForReceived(block.prefix, block.data);
                        await _store.PutAsync(id, block.data);
                        OnBlock?.Invoke(id, block.data);
                        if (_pending.TryRemove(BlockIds.Key(id), out TaskCompletionSource<byte[]>? waiter))
                        {
                            waiter.TrySetResult(block.data);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            foreach ((_, TaskCompletionSource<byte[]> waiter) in _pending)
            {
                waiter.TrySetException(e);
            }
            _pending.Clear();
        }
    }

    // ---- block and file exchange ----------------------------------------

    public async Task<byte[]> GetBlockAsync(byte[] blockId, CancellationToken token = default)
    {
        if (await _store.GetAsync(blockId) is { } local)
        {
            return local;
        }

        TaskCompletionSource<byte[]> waiter = _pending.GetOrAdd(
            BlockIds.Key(blockId),
            _ => new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));

        await SendAsync(new Message
        {
            wantlist = new Wantlist
            {
                full = true,
                entries = [new Entry { block = blockId }],
            },
        });

        return await waiter.Task.WaitAsync(token);
    }

    /// <summary>Stores a block and returns its id.</summary>
    public async Task<byte[]> PutBlockAsync(byte[] data)
    {
        byte[] id = BlockIds.IdFor(data);
        await _store.PutAsync(id, data);
        return id;
    }

    /// <summary>
    ///     Chunks a stream into blocks and stores a manifest block (the
    ///     chunk ids concatenated) over them; returns the manifest's id —
    ///     the file's root id.
    /// </summary>
    public Task<byte[]> PutFileAsync(Stream source)
    {
        return PutFileAsync(_store, source);
    }

    /// <summary>As <see cref="PutFileAsync(Stream)"/>, straight into a store (how a serving node seeds a file).</summary>
    public static async Task<byte[]> PutFileAsync(IBlockStore store, Stream source)
    {
        async Task<byte[]> PutBlock(byte[] data)
        {
            byte[] id = BlockIds.IdFor(data);
            await store.PutAsync(id, data);
            return id;
        }

        var chunkIds = new List<byte[]>();
        byte[] buffer = new byte[ChunkSize];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            chunkIds.Add(await PutBlock(buffer[..read]));
        }

        return await PutBlock(chunkIds.SelectMany(id => id).ToArray());
    }

    /// <summary>Downloads a file by its root (manifest) id.</summary>
    public async Task GetFileAsync(byte[] rootId, Stream destination, CancellationToken token = default)
    {
        byte[] manifest = await GetBlockAsync(rootId, token);
        if (manifest.Length % BlockIds.MultihashLength != 0)
        {
            throw new InvalidDataException("The root block is not a file manifest for this sample.");
        }

        for (int offset = 0; offset < manifest.Length; offset += BlockIds.MultihashLength)
        {
            byte[] chunk = await GetBlockAsync(manifest[offset..(offset + BlockIds.MultihashLength)], token);
            await destination.WriteAsync(chunk, token);
        }
    }
}

/// <summary>
///     Bitswap 1.1.0 (https://github.com/ipfs/specs/blob/main/BITSWAP.md),
///     as a managed-tier protocol: negotiate, then exchange protobuf
///     Messages of wantlists and block payloads. The protocol only speaks
///     wire format; the session it returns does the exchange.
/// </summary>
internal class BitSwap11Protocol(IBlockStore? blockStore = null) : SymmetricProtocol<BitswapSession>
{
    public static ProtocolRef<BitSwap11Protocol, BitswapSession> Ref => new();

    public override string Id => "/ipfs/bitswap/1.1.0";

    protected override async Task NegotiateAsync(IManagedRemoteComm comm)
    {
        await NegotiationUtils.NegotiateProtocolAsync(comm, Id);
    }

    protected override Task<IManagedProtocolComm<BitswapSession>> ConnectAsync(IManagedRemoteComm comm, bool isListener)
    {
        var session = new BitswapSession(comm.Channel, blockStore ?? new InMemoryBlockStore());
        return Task.FromResult<IManagedProtocolComm<BitswapSession>>(new ManagedProtocolComm<BitswapSession>(session, comm));
    }
}

static class BitswapWire
{
    public static async Task<Message> ReadMessageAsync(IChannel channel)
    {
        var length = await channel.ReadVarintAsync().ConfigureAwait(false);
        var result = await channel.ReadAsync(length, ReadBlockingMode.WaitAll).ConfigureAwait(false);
        if (result.Result != IOResult.Ok)
        {
            throw new InvalidOperationException("Failed to read data from the channel.");
        }

        return Serializer.Deserialize<Message>(result.Data);
    }
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
