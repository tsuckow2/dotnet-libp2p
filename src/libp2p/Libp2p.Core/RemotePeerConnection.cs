// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

internal class RemotePeerConnection : IRemotePeerConnection
{
    public RemotePeerConnection(
        IRemotePeer remotePeer,
        ITransportConnection transportConnection,
        Func<Task> disconnect,
        IServiceProvider serviceProvider,
        ConnectionRegistry connectionRegistry,
        IPeerContext? context = null,
        IRemoteComm? transportComm = null,
        IReadOnlyList<IProtocol>? managedAdapters = null)
    {
        RemotePeer = remotePeer;
        TransportConnection = transportConnection;
        Context = context;
        TransportComm = transportComm;
        Managed = new ManagedPeer(remotePeer, this, serviceProvider, connectionRegistry, managedAdapters ?? []);
        _disconnect = disconnect;
        _serviceProvider = serviceProvider;
    }

    private readonly Func<Task> _disconnect;
    private readonly IServiceProvider _serviceProvider;

    /// <inheritdoc />
    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
    {
        if (Context is not PeerContext context)
        {
            throw new InvalidOperationException("This connection has no stack context to dial over.");
        }

        var completion = new TaskCompletionSource();
        context.SubDialRequests.Add(new ChannelRequest
        {
            SubProtocol = ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(_serviceProvider),
            CompletionSource = completion,
        });
        return completion.Task.WaitAsync(token);
    }

    public IRemotePeer RemotePeer { get; }
    public IManagedPeer Managed { get; }
    public IChannelMultiplexer? Mux => Managed.Mux;
    public ITransportConnection TransportConnection { get; }

    /// <summary>
    ///     The stack context for chain-built connections; null for raw
    ///     transport connections.
    /// </summary>
    internal IPeerContext? Context { get; }

    /// <summary>
    ///     The transport comm for raw connections; null for chain-built ones.
    /// </summary>
    internal IRemoteComm? TransportComm { get; }

    public Task DisconnectAsync()
    {
        return _disconnect();
    }
}
