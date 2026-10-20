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

    private readonly ILocalPeer _localPeer;
    private readonly IProtocolRegistry _protocolRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<IProtocol> _managedAdapters;
    private readonly ConcurrentDictionary<Identity, IRemotePeer> _remotePeers = new();

    public IProtocolRegistry ProtocolRegistry => _protocolRegistry;

    public ILocalPeer LocalPeer => _localPeer;

    public ConcurrentDictionary<Identity, IRemotePeer> RemotePeers => _remotePeers;

    public ManagedPeerSwitch(ILocalPeer localPeer, IProtocolRegistry protocolRegistry, IServiceProvider serviceProvider)
        : this(localPeer, protocolRegistry, serviceProvider, null)
    {
    }

    internal ManagedPeerSwitch(
        ILocalPeer localPeer,
        IProtocolRegistry protocolRegistry,
        IServiceProvider serviceProvider,
        IEnumerable<Func<IProtocol>>? managedAdapterFactories)
    {
        _localPeer = localPeer;
        _protocolRegistry = protocolRegistry;
        _serviceProvider = serviceProvider;
        _managedAdapters = (managedAdapterFactories ?? [])
            .Select(factory => factory())
            .ToList();
    }

    public ILocalPeer Create(Identity? identity = default, Multiaddress? localAddr = default)
    {
        var peer = new LocalPeer(identity ?? new Identity());
        if (localAddr is not null)
        {
            peer.GetOrAddAddress(localAddr);
        }
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
            // A stack without a security layer would hand out
            // connections to whoever answered, with no proof of who
            // they are — the switch does not assemble those. (A
            // transport that secures itself, quic-v1, needs none.)
            // Plaintext counts only if it is deliberately registered
            // as the security protocol; its absence is never filled
            // in silently.
            IProtocol security = _protocolRegistry.SecurityProtocols.FirstOrDefault()
                ?? throw new InvalidOperationException("No security protocol (e.g. /noise) is registered; the switch does not assemble unsecured stacks.");
            layers.Add(security);
            layers.Add(NewSelector());
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

    /// <summary>
    ///     The transport protocols of the multiaddr vocabulary. An
    ///     address is a layered path; its outermost layer of this set
    ///     is the transport boundary the switch must serve — layers
    ///     past it (a /ws/ over the TCP endpoint, a /p2p-circuit past
    ///     the relay) belong to a different transport than the one
    ///     that produced the bytes underneath.
    /// </summary>
    private static readonly HashSet<string> TransportProtocols =
    [
        "tcp", "udp", "quic", "quic-v1", "ws", "wss", "webrtc",
        "webrtc-direct", "webtransport", "p2p-circuit", "onion", "onion3",
    ];

    private IProtocol PickTransport(Multiaddress addr)
    {
        // The transport is picked positionally: the address's
        // outermost transport component decides, and the registered
        // transport declaring that component (ITransport.AddressProtocol)
        // serves it. A component no registered transport declares —
        // /p2p-circuit today — is refused here rather than served by
        // whichever transport produced the bytes underneath it.
        string? outermost = addr.Protocols
            .LastOrDefault(protocol => TransportProtocols.Contains(protocol.Name))?.Name;
        if (outermost is null)
        {
            throw new NotSupportedException($"No transport can serve {addr}: the address has no transport component.");
        }
        return _protocolRegistry.Transports.FirstOrDefault(transport => transport.AddressProtocol == outermost) as IProtocol
            ?? throw new NotSupportedException($"No registered transport can serve {addr}: nothing is registered for '{outermost}'.");
    }

    // ---- connection bookkeeping -----------------------------------------
    //
    // The switch creates the connection object before the stack runs and
    // hands it down the layers as the stack's one parameter: transports
    // fill in its endpoints, the muxer reports establishment through
    // Connected(), and only then does the connection become visible —
    // dials complete, listeners raise, and the transport connection is
    // attached from the endpoint the transport observed.

    private RemotePeerConnection CreateStackConnection(
        ILocalPeer localPeer, IRemotePeer remotePeer, Func<Task> disconnect,
        Action<RemotePeerConnection> onConnected, bool anonymousPeer = false)
    {
        var connection = new RemotePeerConnection(
            localPeer, remotePeer, disconnect, _serviceProvider,
            managedAdapters: _managedAdapters, isStackConnection: true);
        connection.ConnectedCallback = () =>
        {
            connection.TransportConnection = new GenericRemoteConnection(
                connection.RemoteEndpoint ?? remotePeer.Address, disconnect);
            if (!anonymousPeer)
            {
                _remotePeers[remotePeer.Identity] = remotePeer;
            }
            onConnected(connection);
        };
        connection.PeerIdentifiedCallback = previous => Canonicalize(connection, previous);
        return connection;
    }

    /// <summary>
    ///     A connection's peer started anonymous and Identify has named
    ///     it: fold it into the switch's one canonical peer for that
    ///     identity. If a canonical peer already exists (an earlier
    ///     identified dial, say), the anonymous peer's address book is
    ///     absorbed into it and the connection adopts it; otherwise the
    ///     peer itself becomes canonical. Either way, from here on
    ///     every route to this identity — registry, dial, connection —
    ///     names the same object.
    /// </summary>
    private void Canonicalize(RemotePeerConnection connection, Identity previousIdentity)
    {
        IRemotePeer peer = connection.RemotePeer;

        if (_remotePeers.TryGetValue(previousIdentity, out IRemotePeer? stale)
            && ReferenceEquals(stale, peer))
        {
            _remotePeers.TryRemove(previousIdentity, out _);
        }

        if (_remotePeers.TryGetValue(peer.Identity, out IRemotePeer? canonical))
        {
            if (ReferenceEquals(canonical, peer))
            {
                return;
            }

            if (canonical is RemotePeer canonicalPeer && peer is RemotePeer anonymousPeer)
            {
                canonicalPeer.AbsorbAddressesFrom(anonymousPeer);
            }
            connection.RemotePeer = canonical;
        }
        else
        {
            _remotePeers[peer.Identity] = peer;
        }
    }

    private void DialIdentityProtocols(RemotePeerConnection connection)
    {
        foreach (IProtocol identityProtocol in _protocolRegistry.IdentityProtocols)
        {
            connection.SubDialRequests.Add(new ChannelRequest
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

        // The listener's shell connection: it carries the local peer
        // down to the transport, which reports readiness through it.
        // The connections for accepted transports are created per
        // accept through the root factory, each with its own anonymous
        // remote peer (a security-bearing transport names it from the
        // handshake proof; otherwise Identify names it).
        var listener = new SwitchListener(signalingChannel, localPeer);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        RemotePeerConnection shell = CreateStackConnection(
            localPeer, new RemotePeer(new Identity()),
            disconnect: () => Task.CompletedTask,
            onConnected: _ => { }, anonymousPeer: true);
        shell.ListenerReadyCallback = () => ready.TrySetResult();

        ((ChannelFactory)rootFactory).ConnectionFactory = () =>
        {
            RemotePeerConnection accepted = CreateStackConnection(
                localPeer, new RemotePeer(new Identity()),
                disconnect: () => Task.CompletedTask,
                onConnected: connection => listener.RaiseOnConnection(connection.RemotePeer),
                anonymousPeer: true);
            // An accepted connection's local endpoint is the
            // listener's bound address, which the transport recorded
            // on the shell before accepting. (Context forks used to
            // inherit this by copy; connections state it explicitly.)
            accepted.LocalEndpoint = shell.LocalEndpoint;
            return accepted;
        };

        _ = root.ListenAsync(signalingChannel, rootFactory, shell)
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
        (IRemotePeer peer, bool anonymous) = RemotePeerForAddress(addr);
        return DialAsync(_localPeer, addr, peer, token, anonymous);
    }

    /// <summary>
    ///     The remote peer for an address being dialed: when the
    ///     address's terminal /p2p/ part embeds the public key in its
    ///     peer id (identity-hash ids, e.g. Ed25519), the peer is the
    ///     switch's interned one for that identity — a dial must know
    ///     who it expects to reach, and every dial to the same peer
    ///     must agree on the object. A routed address terminates at
    ///     its final /p2p/ (the destination), never the relay's. For
    ///     hashed peer ids the key cannot be recovered from the id,
    ///     and for addresses without a /p2p/ part the peer starts
    ///     anonymous; in both cases Identify fills the identity in
    ///     during the handshake.
    /// </summary>
    private (IRemotePeer Peer, bool Anonymous) RemotePeerForAddress(Multiaddress addr)
    {
        if (addr.TerminalP2P()?.ToString() is string peerId
            && PeerId.ExtractPublicKey(new PeerId(peerId).Bytes) is { } publicKey)
        {
            return (GetRemotePeer(new Identity(publicKey)), false);
        }

        return (new RemotePeer(new Identity()), true);
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
        (IRemotePeer peer, bool anonymous) = RemotePeerForAddress(addr);
        return DialAsync(fromPeer, addr, peer, token, anonymous);
    }

    internal async Task<IRemotePeerConnection> DialAsync(ILocalPeer fromPeer, Multiaddress addr, IRemotePeer remotePeer, CancellationToken token = default, bool anonymousPeer = false)
    {
        remotePeer.GetOrAddAddress(addr);
        (IProtocol root, IChannelFactory rootFactory) = BuildChain(addr);

        Channel signalingChannel = new();
        token.Register(() => _ = signalingChannel.CloseAsync());

        if (remotePeer is RemotePeer concreteRemote)
        {
            concreteRemote.Address = addr;
        }

        var connected = new TaskCompletionSource<IRemotePeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemotePeerConnection connection = CreateStackConnection(
            fromPeer, remotePeer,
            disconnect: () => signalingChannel.CloseAsync().AsTask(),
            onConnected: conn =>
            {
                DialIdentityProtocols(conn);
                connected.TrySetResult(conn);
            },
            anonymousPeer: anonymousPeer);

        // The dial names its expectation: the peer the address
        // terminates at (its terminal /p2p — never a relay's). The
        // security layer enforces it against the handshake's proven
        // key before this connection is handed out.
        connection.ExpectedPeerId = addr.TerminalP2P() is { } terminal
            ? new PeerId(terminal.ToString())
            : null;

        _ = root.DialAsync(signalingChannel, rootFactory, connection)
            .ContinueWith(task =>
            {
                // A stack that ends without reporting Connected has failed
                // the dial (a refused TCP connect, say — the transport
                // swallows it and just stops), so fault the wait instead
                // of hanging until the caller's token fires.
                if (task.IsFaulted)
                {
                    // Surface one connection failure, not the stack's
                    // aggregate: a PeerConnectionException from any
                    // layer stays itself; anything else (a socket torn
                    // down mid-handshake, say) becomes the inner
                    // exception of one.
                    AggregateException stackFailure = task.Exception!.Flatten();
                    PeerConnectionException? connectionFailure = stackFailure.InnerExceptions
                        .OfType<PeerConnectionException>().FirstOrDefault();
                    connected.TrySetException(connectionFailure
                        ?? new PeerConnectionException(
                            $"The connection to {addr} failed during establishment.",
                            stackFailure.InnerExceptions[0]));
                }
                else if (!connected.Task.IsCompleted)
                {
                    connected.TrySetException(new PeerConnectionException($"The connection to {addr} closed before it was established."));
                }
            });
        return await connected.Task.WaitAsync(token);
    }
}
