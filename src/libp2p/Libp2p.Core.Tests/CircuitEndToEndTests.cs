// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Circuit;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
///     Circuit Relay v2 end-to-end: a destination that listens only
///     through a relay (it has no direct listener at all), and a
///     dialer that reaches it at its /p2p-circuit address. The stack
///     above the relayed stream runs end-to-end, so the identity the
///     dialer ends up with is the one Noise proved — the relay is a
///     pipe, not a party.
///
///     The relay itself is played by a test double implementing the
///     hop server side (the product deliberately does not ship the
///     relay server role): reservations are recorded against the
///     connection they arrived on, and a CONNECT splices the hop
///     stream to a stop stream opened on that very connection.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CircuitEndToEndTests
{
    private sealed class TestRelayHopServer(CircuitJobs jobs, ILoggerFactory? loggerFactory = null) : IProtocol
    {
        private readonly ILogger? _logger = loggerFactory?.CreateLogger<TestRelayHopServer>();
        private readonly ConcurrentDictionary<string, IRemotePeerConnection> _reservations = new();

        public string Id => "/libp2p/circuit/relay/0.2.0/hop";

        public Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
        {
            throw new NotSupportedException("The test relay only serves hop.");
        }

        public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
        {
            HopMessage message = await CircuitWire.ReadAsync<HopMessage>(channel);
            PeerId remote = connection.RemotePeer.Identity.PeerId;

            if (message.Type == HopMessageType.Reserve)
            {
                _reservations[remote.ToString()] = connection;
                await CircuitWire.WriteAsync(channel, new HopMessage
                {
                    Type = HopMessageType.Status,
                    Status = CircuitStatus.Ok,
                    Reservation = new WireReservation
                    {
                        Expire = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600),
                    },
                });
                _logger?.LogDebug("Reservation recorded for {peer}", remote);
                // The reservation lives as long as the connection does.
                await channel;
                return;
            }

            if (message.Type == HopMessageType.Connect && message.Peer is not null)
            {
                var destination = new PeerId(message.Peer.Id);
                if (!_reservations.TryGetValue(destination.ToString(), out IRemotePeerConnection? destinationConnection))
                {
                    await CircuitWire.WriteAsync(channel, new HopMessage
                    {
                        Type = HopMessageType.Status,
                        Status = CircuitStatus.NoReservation,
                    });
                    return;
                }

                var job = new StopJob { Source = remote, UpChannel = channel };
                jobs.EnqueueStop(destination, job);
                Task stopTask = destinationConnection.DialAsync<StopProtocol>();
                try
                {
                    await job.Established.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception)
                {
                    await CircuitWire.WriteAsync(channel, new HopMessage
                    {
                        Type = HopMessageType.Status,
                        Status = CircuitStatus.ConnectionFailed,
                    });
                    return;
                }

                await CircuitWire.WriteAsync(channel, new HopMessage
                {
                    Type = HopMessageType.Status,
                    Status = CircuitStatus.Ok,
                });
                // The stop protocol pumps the splice for its lifetime.
                await stopTask;
                return;
            }

            await CircuitWire.WriteAsync(channel, new HopMessage
            {
                Type = HopMessageType.Status,
                Status = CircuitStatus.UnexpectedMessage,
            });
        }
    }

    private sealed class RelayBuilder(IServiceProvider serviceProvider) : PeerFactoryBuilderBase<RelayBuilder>(serviceProvider)
    {
        public RelayBuilder Configure()
        {
            AddTransport<IpTcpProtocol>();
            AddSecurityProtocol<NoiseProtocol>();
            AddMuxerProtocol<YamuxProtocol>();
            AddIdentityProtocol<IdentifyProtocol>();
            AddAppLayerProtocol<MultistreamProtocol>();
            AddAppLayerProtocol<TestRelayHopServer>();
            AddAppLayerProtocol<StopProtocol>();
            return this;
        }
    }

    private static (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer) BuildNode(
        Identity? identity = null, string? listenAddress = null)
    {
        var localPeer = new LocalPeer(identity ?? new Identity());
        localPeer.GetOrAddAddress(listenAddress ?? "/ip4/127.0.0.1/tcp/0");

        ServiceProvider services = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder)
            .AddLogging()
            .BuildServiceProvider();

        return (services, services.GetRequiredService<IManagedPeerSwitch>(), localPeer);
    }

    private static (IManagedPeerSwitch Switch, LocalPeer Peer) BuildRelayNode()
    {
        var localPeer = new LocalPeer(new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/tcp/0");

        ServiceProvider services = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddScoped<CircuitJobs>()
            // Identify resolves the builder lazily from DI (for the
            // app-layer protocol list), so it must be registered —
            // AddLibp2p does this for the default builder; the relay's
            // hand-rolled stack registers its own.
            .AddScoped<RelayBuilder>(sp => new RelayBuilder(sp).Configure())
            .AddScoped<IPeerFactoryBuilder>(sp => sp.GetRequiredService<RelayBuilder>())
            .AddLogging()
            .BuildServiceProvider();

        IPeerFactory factory = services.GetRequiredService<IPeerFactoryBuilder>().Build();
        return ((IManagedPeerSwitch)factory, localPeer);
    }

    [Test]
    public async Task Dial_ThroughRelay_ReachesDestination_WithIdentityProvenEndToEnd()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (relaySwitch, relayPeer) = BuildRelayNode();
        IListener relayListener = await relaySwitch.ListenAsync(relayPeer, cts.Token);

        // The destination's only listen address is the circuit one:
        // it cannot be reached directly at all.
        var destinationNode = BuildNode(listenAddress: $"{relayListener.Address}/p2p-circuit");
        IListener destinationListener = await destinationNode.Switch.ListenAsync(destinationNode.Peer, cts.Token);
        Assert.That(destinationListener.Address.ToString(), Does.Contain("/p2p-circuit/p2p/"));

        var dialerNode = BuildNode();
        IRemotePeerConnection connection = await dialerNode.Switch.DialAsync(destinationListener.Address, cts.Token);

        // The connection is to the destination, proven by the Noise
        // handshake that ran over the relayed stream — not to the
        // relay, whose identity is different.
        Assert.That(connection.RemotePeer.Identity, Is.EqualTo(destinationNode.Peer.Identity));
        Assert.That(connection.RemotePeer.Identity, Is.Not.EqualTo(relayPeer.Identity));

        // And the connection carries application traffic (Ping does a
        // full request/response over a muxed stream).
        await connection.DialAsync<PingProtocol>(cts.Token);

        await connection.DisconnectAsync();
        await destinationListener.DisconnectAsync();
        await relayListener.DisconnectAsync();
    }

    [Test]
    public async Task RelayedConnection_IsLayeredOverTheRelayConnection_AndEndsWithIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (relaySwitch, relayPeer) = BuildRelayNode();
        IListener relayListener = await relaySwitch.ListenAsync(relayPeer, cts.Token);

        var destinationNode = BuildNode(listenAddress: $"{relayListener.Address}/p2p-circuit");
        IListener destinationListener = await destinationNode.Switch.ListenAsync(destinationNode.Peer, cts.Token);

        var dialerNode = BuildNode();
        IRemotePeerConnection connection = await dialerNode.Switch.DialAsync(destinationListener.Address, cts.Token);

        // The relayed connection references the connection it rides
        // on: the dialer's connection to the relay.
        IRemotePeerConnection? parent = connection.ParentConnection;
        Assert.That(parent, Is.Not.Null);
        Assert.That(parent!.RemotePeer.Identity, Is.EqualTo(relayPeer.Identity));
        Assert.That(parent, Is.Not.SameAs(connection));

        await connection.DialAsync<PingProtocol>(cts.Token);

        // When the parent ends, the layered connection is
        // disconnected with it — its own teardown runs (which clears
        // the parent reference) rather than the connection lingering
        // until a stream notices.
        await parent.DisconnectAsync();
        Assert.That(connection.ParentConnection, Is.Null);

        await destinationListener.DisconnectAsync();
        await relayListener.DisconnectAsync();
    }

    [Test]
    public async Task Dial_ThroughRelay_ToPeerWithoutReservation_IsRefused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (relaySwitch, relayPeer) = BuildRelayNode();
        IListener relayListener = await relaySwitch.ListenAsync(relayPeer, cts.Token);

        var dialerNode = BuildNode();
        var stranger = new Identity();

        // A well-formed circuit address, but the named peer never
        // reserved on this relay: the relay refuses the CONNECT and
        // the dial faults.
        var address = Multiaddress.Decode($"{relayListener.Address}/p2p-circuit/p2p/{stranger.PeerId}");
        Assert.ThrowsAsync<PeerConnectionException>(
            async () => await dialerNode.Switch.DialAsync(address, cts.Token));

        await relayListener.DisconnectAsync();
    }
}
