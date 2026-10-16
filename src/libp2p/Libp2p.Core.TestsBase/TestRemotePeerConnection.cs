// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.TestsBase;

/// <summary>
///     An in-memory IRemotePeerConnection for tests: protocol dials
///     complete immediately and there is no managed tier.
/// </summary>
public class TestRemotePeerConnection(IRemotePeer remotePeer) : IRemotePeerConnection
{
    public IRemotePeer RemotePeer => remotePeer;

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
