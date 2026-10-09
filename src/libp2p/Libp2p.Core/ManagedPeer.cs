// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

internal class ManagedPeer(IRemotePeer remotePeer, IRemotePeerConnection connection, IServiceProvider serviceProvider) : IManagedPeer
{
    public IRemotePeer RemotePeer => remotePeer;

    public IChannelMultiplexer? Mux { get; set; }

    public async Task<IManagedProtocolComm<TOut>> DialAsync<TProtocol, TOut>(ProtocolRef<TProtocol, TOut> protocolRef, CancellationToken token = default)
        where TProtocol : IProtocol<TOut>
    {
        TProtocol protocol = ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(serviceProvider);
        IChannel channel = Mux is not null ? await Mux.DialAsync(token) : connection.Channel;
        IManagedRemoteComm comm = new GenericManagedRemoteComm(false, channel, connection.TransportConnection, this);
        return await protocol.DialAsync(comm);
    }
}
