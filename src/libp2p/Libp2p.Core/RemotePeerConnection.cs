// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

internal class RemotePeerConnection : IRemotePeerConnection
{
    public RemotePeerConnection(IRemotePeer remotePeer, IRemoteComm transportComm, IServiceProvider serviceProvider)
    {
        RemotePeer = remotePeer;
        TransportComm = transportComm;
        Managed = new ManagedPeer(remotePeer, this, serviceProvider);
    }

    public IRemotePeer RemotePeer { get; }
    public IManagedPeer Managed { get; }
    public IChannelMultiplexer? Mux => Managed.Mux;
    public IChannel Channel => TransportComm.Channel;

    internal IRemoteComm TransportComm { get; }
    public ITransportConnection TransportConnection => TransportComm.Connection;

    public Task DisconnectAsync()
    {
        return TransportComm.Connection.DisconnectAsync();
    }
}
