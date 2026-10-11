// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The switch's live connections, keyed by the connection id carried on
///     every peer context (forks included). Stack layers that only see a
///     context — the managed-protocol adapter above all — use this to find
///     the connection they belong to.
/// </summary>
internal class ConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, IRemotePeerConnection> _connections = new();

    public void Register(Guid connectionId, IRemotePeerConnection connection)
    {
        _connections[connectionId] = connection;
    }

    public IRemotePeerConnection? Get(IPeerContext context)
    {
        return context is PeerContext peerContext && _connections.TryGetValue(peerContext.ConnectionId, out IRemotePeerConnection? connection)
            ? connection
            : null;
    }

    public void Remove(Guid connectionId)
    {
        _connections.TryRemove(connectionId, out _);
    }
}
