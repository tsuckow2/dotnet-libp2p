// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Nethermind.Ipfs.Bitswap;

/// <summary>Where blocks live, addressed by their ids (<see cref="BlockIds"/>).</summary>
public interface IBlockStore
{
    Task<byte[]?> GetAsync(byte[] blockId);
    Task PutAsync(byte[] blockId, byte[] data);
}

/// <summary>An <see cref="IBlockStore"/> that keeps blocks in memory.</summary>
public class InMemoryBlockStore : IBlockStore
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
