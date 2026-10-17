// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Nethermind.Libp2p.Core;
using ProtoBuf;

namespace Nethermind.Ipfs.Bitswap;

/// <summary>
///     A live Bitswap session: the typed comm the protocol hands back.
///     It owns the channel's read loop, answers wants from the block
///     store, and matches received blocks to outstanding wants — a block
///     only completes a want when the id recomputed from its content
///     matches, so verification is inherent, not a separate step.
/// </summary>
public class BitswapSession
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

    private async Task SendBlockAsync(Cid cid, byte[] data)
    {
        await SendAsync(new Message
        {
            payload = [new Block { prefix = cid.Prefix, data = data }],
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

                        // Wants carry full CIDs; the store is keyed by
                        // multihash, and the reply carries the wanted
                        // CID's own prefix.
                        Cid cid;
                        try
                        {
                            cid = Cid.FromBytes(entry.block);
                        }
                        catch (FormatException)
                        {
                            continue;
                        }

                        byte[]? data = await _store.GetAsync(cid.Multihash);
                        if (data != null)
                        {
                            await SendBlockAsync(cid, data);
                        }
                    }
                }

                if (message.payload != null)
                {
                    foreach (Block block in message.payload)
                    {
                        // The block's id is its prefix plus the digest
                        // recomputed from the content, so a corrupted
                        // or substituted block never matches a want.
                        byte[] receivedCid = [.. block.prefix, .. SHA256.HashData(block.data)];
                        await _store.PutAsync(Cid.FromBytes(receivedCid).Multihash, block.data);
                        OnBlock?.Invoke(receivedCid, block.data);
                        if (_pending.TryRemove(BlockIds.Key(receivedCid), out TaskCompletionSource<byte[]>? waiter))
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

    /// <summary>Fetches a block by CID, from the local store when present, else from the peer.</summary>
    public async Task<byte[]> GetBlockAsync(Cid cid, CancellationToken token = default)
    {
        if (await _store.GetAsync(cid.Multihash) is { } local)
        {
            return local;
        }

        TaskCompletionSource<byte[]> waiter = _pending.GetOrAdd(
            BlockIds.Key(cid.Bytes),
            _ => new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));

        await SendAsync(new Message
        {
            wantlist = new Wantlist
            {
                full = true,
                entries = [new Entry { block = cid.Bytes }],
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
    ///     the file's root id. This manifest format is a staging
    ///     convenience for moving files over the block exchange; real
    ///     IPFS files are UnixFS DAGs (Nethermind.Ipfs.UnixFs).
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
        byte[] manifest = await GetBlockAsync(Cid.FromBytes(rootId), token);
        if (manifest.Length % BlockIds.MultihashLength != 0)
        {
            throw new InvalidDataException("The root block is not a file manifest (see PutFileAsync).");
        }

        for (int offset = 0; offset < manifest.Length; offset += BlockIds.MultihashLength)
        {
            byte[] chunk = await GetBlockAsync(Cid.FromBytes(manifest[offset..(offset + BlockIds.MultihashLength)]), token);
            await destination.WriteAsync(chunk, token);
        }
    }
}
