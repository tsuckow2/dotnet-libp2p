// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

internal class RemotePeerConnection : IRemotePeerConnection
{
    public RemotePeerConnection(
        ILocalPeer localPeer,
        IRemotePeer remotePeer,
        Func<Task> disconnect,
        IServiceProvider serviceProvider,
        IReadOnlyList<IProtocol>? managedAdapters = null,
        ITransportConnection? transportConnection = null,
        IRemoteComm? transportComm = null,
        bool isStackConnection = false)
    {
        LocalPeer = localPeer;
        RemotePeer = remotePeer;
        TransportConnection = transportConnection!;
        TransportComm = transportComm;
        IsStackConnection = isStackConnection;
        Managed = new ManagedPeer(this, serviceProvider, managedAdapters ?? []);
        _disconnect = disconnect;
        _serviceProvider = serviceProvider;
        if (remotePeer is RemotePeer concretePeer)
        {
            concretePeer.IdentityEstablished += (_, previous) => PeerIdentifiedCallback?.Invoke(previous);
        }
    }

    private readonly Func<Task> _disconnect;
    private readonly IServiceProvider _serviceProvider;

    /// <inheritdoc />
    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
    {
        if (!IsStackConnection)
        {
            throw new InvalidOperationException("This connection has no stack to dial over.");
        }

        var completion = new TaskCompletionSource();
        SubDialRequests.Add(new ChannelRequest
        {
            SubProtocol = ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(_serviceProvider),
            CompletionSource = completion,
        });
        return completion.Task.WaitAsync(token);
    }

    public ILocalPeer LocalPeer { get; }
    public IRemotePeer RemotePeer { get; internal set; }
    public IManagedPeer Managed { get; }
    public IChannelMultiplexer? Mux => Managed.Mux;

    /// <summary>
    ///     The transport-level connection. For stack-built connections
    ///     the switch attaches it when the muxer reports the connection
    ///     established; nothing can observe the connection before then,
    ///     so the formation-stage null is never visible outside the
    ///     stack.
    /// </summary>
    public ITransportConnection TransportConnection { get; internal set; }

    public Multiaddress? RemoteEndpoint { get; set; }
    public Multiaddress? LocalEndpoint { get; set; }
    public PeerId? ExpectedPeerId { get; set; }
    public PeerId? ProvenPeerId { get; set; }

    private IRemotePeerConnection? _parentConnection;
    private readonly ConcurrentDictionary<IRemotePeerConnection, byte> _layeredConnections = new();

    public IRemotePeerConnection? ParentConnection
    {
        get => _parentConnection;
        set
        {
            if (ReferenceEquals(_parentConnection, value))
            {
                return;
            }
            if (ReferenceEquals(value, this))
            {
                throw new ArgumentException("A connection cannot be layered over itself.");
            }
            if (_parentConnection is RemotePeerConnection oldParent)
            {
                oldParent._layeredConnections.TryRemove(this, out _);
            }
            _parentConnection = value;
            if (value is RemotePeerConnection newParent)
            {
                newParent._layeredConnections.TryAdd(this, default);
            }
        }
    }

    public BlockingCollection<IChannelRequest> SubDialRequests { get; } = new();

    /// <summary>
    ///     The transport comm for raw connections; null for stack-built
    ///     ones.
    /// </summary>
    internal IRemoteComm? TransportComm { get; }

    /// <summary>
    ///     Whether the switch's stack built this connection (and its
    ///     muxer therefore consumes <see cref="SubDialRequests"/>).
    /// </summary>
    internal bool IsStackConnection { get; }

    /// <summary>
    ///     Installed by the switch on connections it creates: run when
    ///     the muxer reports the connection established.
    /// </summary>
    internal Action? ConnectedCallback { get; set; }

    /// <summary>
    ///     Installed by the switch on a listener's shell connection:
    ///     run when the transport reports it is bound and ready.
    /// </summary>
    internal Action? ListenerReadyCallback { get; set; }

    /// <summary>
    ///     Installed by the switch on connections it creates: run when
    ///     the connection's peer is named by a different identity than
    ///     it started with (Identify, after an anonymous start), with
    ///     the previous identity as the argument. The switch
    ///     canonicalizes its peer registry on this signal.
    /// </summary>
    internal Action<Identity>? PeerIdentifiedCallback { get; set; }

    public void Connected()
    {
        ConnectedCallback?.Invoke();
    }

    public void ListenerReady()
    {
        ListenerReadyCallback?.Invoke();
    }

    public async Task DisconnectAsync()
    {
        // Connections layered over this one ride on its streams;
        // they end with it, disconnected explicitly rather than left
        // to notice a dead stream.
        IRemotePeerConnection[] layered = _layeredConnections.Keys.ToArray();
        _layeredConnections.Clear();
        foreach (IRemotePeerConnection connection in layered)
        {
            try
            {
                await connection.DisconnectAsync();
            }
            catch
            {
                // A layered connection failing to disconnect does
                // not stop the rest of the teardown.
            }
        }

        ParentConnection = null;
        await _disconnect();
    }
}
