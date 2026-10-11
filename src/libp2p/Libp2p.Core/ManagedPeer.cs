// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

internal class ManagedPeer(IRemotePeer remotePeer, RemotePeerConnection connection, IServiceProvider serviceProvider, ConnectionRegistry connectionRegistry, IReadOnlyList<IProtocol> managedAdapters) : IManagedPeer
{
    public IRemotePeer RemotePeer => remotePeer;

    public IChannelMultiplexer? Mux { get; set; }

    public async Task<IManagedProtocolComm<TOut>> DialAsync<TProtocol, TOut>(ProtocolRef<TProtocol, TOut> protocolRef, CancellationToken token = default)
        where TProtocol : IProtocol<TOut>
    {
        if (connection.Context is PeerContext context)
        {
            // Chain-built connection: queue the protocol as a sub-dial on
            // the connection's context. The muxer opens a stream, multistream
            // selects the adapter registered under the protocol's id, and the
            // adapter runs the protocol and completes the typed request.
            var adapter = managedAdapters.OfType<ManagedProtocolAdapter<TProtocol, TOut>>().FirstOrDefault()
                ?? throw new InvalidOperationException($"{typeof(TProtocol).Name} is not registered as a managed protocol; add it with AddManagedProtocol.");
            TaskCompletionSource<IManagedProtocolComm<TOut>> completion = adapter.BeginDial();
            context.SubDialRequests.Add(new ChannelRequest
            {
                SubProtocol = adapter,
                CompletionSource = new TaskCompletionSource(),
            });
            return await completion.Task.WaitAsync(token);
        }

        // Raw transport connection: dial the protocol straight over the
        // transport channel (or a mux channel, if one was negotiated).
        TProtocol protocol = ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(serviceProvider);
        IChannel channel = Mux is not null
            ? await Mux.DialAsync(token)
            : connection.TransportComm?.Channel ?? throw new InvalidOperationException("The connection has no channel to dial over.");
        IManagedRemoteComm comm = new GenericManagedRemoteComm(false, channel, connection.TransportConnection, this);
        return await protocol.DialAsync(comm);
    }
}
