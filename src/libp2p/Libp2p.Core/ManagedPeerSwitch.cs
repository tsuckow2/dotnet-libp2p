// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public class ManagedPeerSwitch(ILocalPeer localPeer, IProtocolRegistry protocolRegistry, IServiceProvider serviceProvider) : IManagedPeerSwitch
{
    private readonly ConcurrentDictionary<Identity, IRemotePeer> _remotePeers = new();

    internal IRemotePeer GetRemotePeer(Identity identity)
    {
        return _remotePeers.GetOrAdd(identity, static id => new RemotePeer(id));
    }

    public Task<IListener> ListenAsync(ILocalPeer peer, CancellationToken token = default)
    {
        // Registry-driven listen + upgrade sequence: plan Phase 3.
        throw new NotImplementedException("Listening is not wired to the registry yet.");
    }

    public Task<IRemotePeerConnection> DialAsync(IRemotePeer peer, Multiaddress addr, CancellationToken token)
    {
        // Registry-driven transport dial + upgrade sequence: plan Phase 3.
        throw new NotImplementedException("Dialing is not wired to the registry yet.");
    }

    public Task<IRemotePeerConnection> DialAsync(IRemotePeer peer, CancellationToken token)
    {
        // Multi-address dial over peer.Addresses with accounting: plan Phase 4.
        throw new NotImplementedException("Dialing is not wired to the registry yet.");
    }

    public Task<IRemotePeerConnection> DialAsync(Multiaddress addr, CancellationToken token)
    {
        // Registry-driven transport dial + upgrade sequence: plan Phase 3.
        throw new NotImplementedException("Dialing is not wired to the registry yet.");
    }
}
