// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Core;

public class ManagedPeerSwitch : IManagedPeerSwitch
{
    private static int CtxId;

    private readonly ILocalPeer _localPeer;
    private readonly IProtocolRegistry _protocolRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConnectionRegistry _connectionRegistry = new();
    private readonly IReadOnlyList<IProtocol> _managedAdapters;
    private readonly ConcurrentDictionary<Identity, IRemotePeer> _remotePeers = new();

    public IProtocolRegistry ProtocolRegistry => _protocolRegistry;

    public ConcurrentDictionary<Identity, IRemotePeer> RemotePeers => _remotePeers;

    public ManagedPeerSwitch(ILocalPeer localPeer, IProtocolRegistry protocolRegistry, IServiceProvider serviceProvider)
        : this(localPeer, protocolRegistry, serviceProvider, null)
    {
    }

    internal ManagedPeerSwitch(
        ILocalPeer localPeer,
        IProtocolRegistry protocolRegistry,
        IServiceProvider serviceProvider,
        IEnumerable<Func<ConnectionRegistry, IProtocol>>? managedAdapterFactories)
    {
        _localPeer = localPeer;
        _protocolRegistry = protocolRegistry;
        _serviceProvider = serviceProvider;
        _managedAdapters = (managedAdapterFactories ?? [])
            .Select(factory => factory(_connectionRegistry))
            .ToList();

        if (localPeer is LocalPeer concrete)
        {
            concrete.AttachSwitch(this);
        }
    }

    public ILocalPeer Create(Identity? identity = default, Multiaddress? localAddr = default)
    {
        var peer = new LocalPeer(identity ?? new Identity());
        if (localAddr is not null)
        {
            peer.GetOrAddAddress(localAddr);
        }
        peer.AttachSwitch(this);
        return peer;
    }

    public IRemotePeer GetRemotePeer(Identity identity)
    {
        return _remotePeers.GetOrAdd(identity, id => new RemotePeer(id));
    }

    // ---- chain assembly -------------------------------------------------
    //
    // The switch assembles the channel-tier stack from the registry by
    // role: transport -> multistream -> security -> multistream -> muxer
    // -> multistream -> app protocols (channel-tier apps, identity
    // protocols, and adapters for the managed-tier protocols). Each layer
    // gets a channel factory mapping the protocols above it to their own
    // up-factories, built top-down.

    private (IProtocol Root, IChannelFactory RootFactory) BuildChain(Multiaddress addr)
    {
        IProtocol transport = PickTransport(addr);

        var emptyFactory = new ChannelFactory(_serviceProvider).Setup(new Dictionary<IProtocol, IChannelFactory>());

        IProtocol? selectorTemplate = _protocolRegistry.OtherProtocols.FirstOrDefault(IsSelector)
            ?? throw new InvalidOperationException("No multistream selector (/multistream/1.0.0) is registered; the switch needs one to assemble the stack.");

        IProtocol NewSelector() => (IProtocol)ActivatorUtilities.CreateInstance(_serviceProvider, selectorTemplate.GetType());

        IProtocol[] apps = _protocolRegistry.OtherProtocols
            .Where(protocol => !IsSelector(protocol))
            .Concat(_protocolRegistry.IdentityProtocols)
            .Concat(_managedAdapters)
            .Distinct()
            .ToArray();

        // The factory handed to the topmost selector: app protocols have
        // nothing above them.
        IChannelFactory above = new ChannelFactory(_serviceProvider)
            .Setup(apps.ToDictionary(protocol => protocol, _ => (IChannelFactory)emptyFactory));

        // Layers above the transport, bottom first; selectors separate the
        // functional stages. A self-multiplexing transport terminates the
        // stack itself: QUIC carries TLS and native streams, so it plays
        // the security and muxer roles too and only the selector and the
        // app protocols sit above it. (Identified by protocol id, like
        // the selector — Core cannot reference the Quic project.)
        var layers = new List<IProtocol> { NewSelector() };
        if (transport.Id != "quic-v1")
        {
            IProtocol? security = _protocolRegistry.SecurityProtocols.FirstOrDefault();
            if (security is not null)
            {
                layers.Add(security);
                layers.Add(NewSelector());
            }
            IProtocol? muxer = _protocolRegistry.MuxerProtocols.FirstOrDefault();
            if (muxer is not null)
            {
                layers.Add(muxer);
                layers.Add(NewSelector());
            }
        }

        for (int i = layers.Count - 1; i >= 0; i--)
        {
            above = new ChannelFactory(_serviceProvider)
                .Setup(new Dictionary<IProtocol, IChannelFactory> { [layers[i]] = above });
        }

        return (transport, above);
    }

    private static bool IsSelector(IProtocol protocol)
    {
        return protocol.Id == "/multistream/1.0.0";
    }

    private IProtocol PickTransport(Multiaddress addr)
    {
        // Transports are picked by the address's shape, matched against
        // the protocol id: /tcp/ is IpTcp's, /udp/ (quic-v1) is Quic's.
        string? transportId = addr.Has<TCP>() ? "ip-tcp" : addr.Has<UDP>() ? "quic-v1" : null;
        if (transportId is null)
        {
            throw new NotSupportedException($"No transport can serve {addr}: the switch dials and listens on TCP and QUIC addresses.");
        }
        return _protocolRegistry.Transports.OfType<IProtocol>().FirstOrDefault(transport => transport.Id == transportId)
            ?? throw new NotSupportedException($"No '{transportId}' transport is registered, but {addr} needs one.");
    }

    // ---- connection bookkeeping -----------------------------------------

    private IRemotePeerConnection RegisterConnection(PeerContext context, IRemotePeer remotePeer, Channel signalingChannel, bool isDialer)
    {
        if (_connectionRegistry.Get(context) is { } existing)
        {
            return existing;
        }

        var transportConnection = new GenericRemoteConnection(
            context.RemoteEndpoint ?? remotePeer.Address,
            isDialer
                ? () => { _connectionRegistry.Remove(context.ConnectionId); return signalingChannel.CloseAsync().AsTask(); }
                : () => { _connectionRegistry.Remove(context.ConnectionId); return Task.CompletedTask; });

        var connection = new RemotePeerConnection(
            remotePeer, transportConnection, transportConnection.DisconnectAsync,
            _serviceProvider, _connectionRegistry, context, managedAdapters: _managedAdapters);
        _connectionRegistry.Register(context.ConnectionId, connection);
        _remotePeers[remotePeer.Identity] = remotePeer;
        if (remotePeer is RemotePeer concretePeer)
        {
            concretePeer.AttachConnection(connection);
        }
        return connection;
    }

    private void DialIdentityProtocols(PeerContext context)
    {
        foreach (IProtocol identityProtocol in _protocolRegistry.IdentityProtocols)
        {
            context.SubDialRequests.Add(new ChannelRequest
            {
                SubProtocol = identityProtocol,
                CompletionSource = new TaskCompletionSource(),
            });
        }
    }

    // ---- listen ---------------------------------------------------------

    public async Task<IListener> ListenAsync(ILocalPeer localPeer, CancellationToken token = default)
    {
        Multiaddress addr = localPeer.Address
            ?? throw new InvalidOperationException("The local peer has no address to listen on.");
        if (!addr.Has<P2P>())
        {
            addr = addr.Add<P2P>(localPeer.Identity.PeerId.ToString());
            if (localPeer is LocalPeer concrete)
            {
                concrete.Address = addr;
            }
        }

        (IProtocol root, IChannelFactory rootFactory) = BuildChain(addr);

        Channel signalingChannel = new();
        token.Register(() => _ = signalingChannel.CloseAsync());

        PeerContext context = new()
        {
            Id = $"ctx-{Interlocked.Increment(ref CtxId)}",
            ConnectionId = Guid.NewGuid(),
            LocalPeer = localPeer,
            RemotePeer = new RemotePeer(new Identity()),
        };

        var listener = new SwitchListener(signalingChannel, localPeer);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.OnListenerReady += () => ready.TrySetResult();
        context.OnRemotePeerConnection += remotePeer =>
        {
            IRemotePeerConnection connection = RegisterConnection(context, remotePeer, signalingChannel, isDialer: false);
            listener.RaiseOnConnection(connection.RemotePeer);
        };

        _ = root.ListenAsync(signalingChannel, rootFactory, context)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    ready.TrySetException(task.Exception!);
                }
            });
        await ready.Task.WaitAsync(token);
        return listener;
    }

    private class SwitchListener(Channel channel, ILocalPeer localPeer) : IListener
    {
        public Multiaddress Address => localPeer.Address;

        public event OnConnection? OnConnection;

        public void RaiseOnConnection(IRemotePeer remotePeer)
        {
            _ = OnConnection?.Invoke(remotePeer);
        }

        public Task DisconnectAsync()
        {
            return channel.CloseAsync().AsTask();
        }

        public TaskAwaiter GetAwaiter()
        {
            return channel.GetAwaiter();
        }
    }

    // ---- dial -----------------------------------------------------------

    public Task<IRemotePeerConnection> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        return DialAsync(_localPeer, addr, new RemotePeer(new Identity()), token);
    }

    public async Task<IRemotePeerConnection> DialAsync(IRemotePeer peer, CancellationToken token = default)
    {
        KeyValuePair<Multiaddress, PeerAddressAccounting>[] addresses = peer.Addresses
            .OrderBy(pair => pair.Value.FailedDials)
            .ToArray();
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException($"No addresses known for peer {peer.Identity.PeerId}.");
        }

        Exception? lastError = null;
        foreach ((Multiaddress addr, PeerAddressAccounting accounting) in addresses)
        {
            try
            {
                return await DialAsync(_localPeer, addr, peer, token);
            }
            catch (Exception e)
            {
                accounting.RecordFailedDial();
                lastError = e;
            }
        }
        throw new PeerConnectionException($"Failed to connect to {peer.Identity.PeerId} at any known address.", lastError);
    }

    public Task<IRemotePeerConnection> DialAsync(IRemotePeer peer, Multiaddress addr, CancellationToken token = default)
    {
        return DialAsync(_localPeer, addr, peer, token);
    }

    internal Task<IRemotePeerConnection> DialAsync(ILocalPeer fromPeer, Multiaddress addr, CancellationToken token = default)
    {
        return DialAsync(fromPeer, addr, new RemotePeer(new Identity()), token);
    }

    internal async Task<IRemotePeerConnection> DialAsync(ILocalPeer fromPeer, Multiaddress addr, IRemotePeer remotePeer, CancellationToken token = default)
    {
        remotePeer.GetOrAddAddress(addr);
        (IProtocol root, IChannelFactory rootFactory) = BuildChain(addr);

        Channel signalingChannel = new();
        token.Register(() => _ = signalingChannel.CloseAsync());

        PeerContext context = new()
        {
            Id = $"ctx-{Interlocked.Increment(ref CtxId)}",
            ConnectionId = Guid.NewGuid(),
            LocalPeer = fromPeer,
            RemotePeer = remotePeer,
        };
        if (remotePeer is RemotePeer concreteRemote)
        {
            concreteRemote.Address = addr;
        }

        var connected = new TaskCompletionSource<IRemotePeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.OnRemotePeerConnection += connectedPeer =>
        {
            IRemotePeerConnection connection = RegisterConnection(context, connectedPeer, signalingChannel, isDialer: true);
            DialIdentityProtocols(context);
            connected.TrySetResult(connection);
        };

        _ = root.DialAsync(signalingChannel, rootFactory, context)
            .ContinueWith(task =>
            {
                // A stack that ends without reporting Connected has failed
                // the dial (a refused TCP connect, say — the transport
                // swallows it and just stops), so fault the wait instead
                // of hanging until the caller's token fires.
                if (task.IsFaulted)
                {
                    connected.TrySetException(task.Exception!);
                }
                else if (!connected.Task.IsCompleted)
                {
                    connected.TrySetException(new PeerConnectionException($"The connection to {addr} closed before it was established."));
                }
            });
        return await connected.Task.WaitAsync(token);
    }
}
