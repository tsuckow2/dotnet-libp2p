// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core.TestsBase;

/// <summary>
///     An in-memory IRemotePeerConnection for tests: protocol dials
///     complete immediately and there is no managed tier.
/// </summary>
public class TestRemotePeerConnection(IRemotePeer remotePeer, ILocalPeer? localPeer = null) : IRemotePeerConnection
{
    public ILocalPeer LocalPeer => localPeer
        ?? throw new NotImplementedException("Test connections are built without a local peer.");

    public IRemotePeer RemotePeer => remotePeer;

    public Multiaddress? RemoteEndpoint { get; set; }

    public Multiaddress? LocalEndpoint { get; set; }

    public PeerId? ExpectedPeerId { get; set; }

    public PeerId? ProvenPeerId { get; set; }

    public IRemotePeerConnection? ParentConnection { get; set; }

    public BlockingCollection<IChannelRequest> SubDialRequests { get; } = new();

    public void Connected()
    {
    }

    public void ListenerReady()
    {
    }

    public IManagedPeer Managed => throw new NotImplementedException("Test connections have no managed tier.");

    public IChannelMultiplexer? Mux => null;

    public ITransportConnection TransportConnection { get; } =
        new GenericRemoteConnection(remotePeer.Address, () => Task.CompletedTask);

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }

    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
    {
        return Task.CompletedTask;
    }
}
