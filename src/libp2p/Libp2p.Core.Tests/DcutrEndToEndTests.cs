// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net.Quic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Circuit;
using Nethermind.Libp2p.Protocols.Dcutr;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
///     DCUtR end-to-end: two nodes that meet over a relayed (circuit)
///     connection upgrade it to a direct QUIC connection. The
///     destination listens on QUIC directly and, through a relay,
///     on a circuit address; the dialer reaches it through the
///     relay first — the topology hole punching exists for. The
///     relay is played by the same test double as the circuit
///     fixture (the product deliberately does not ship the relay
///     server role).
/// </summary>
[TestFixture]
[NonParallelizable]
public class DcutrEndToEndTests
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
                job.Release.TrySetResult();
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

    private static (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer) BuildNode()
    {
        var localPeer = new LocalPeer(new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/udp/0/quic-v1");

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
            .AddScoped<RelayBuilder>(sp => new RelayBuilder(sp).Configure())
            .AddScoped<IPeerFactoryBuilder>(sp => sp.GetRequiredService<RelayBuilder>())
            .AddLogging()
            .BuildServiceProvider();

        IPeerFactory factory = services.GetRequiredService<IPeerFactoryBuilder>().Build();
        return ((IManagedPeerSwitch)factory, localPeer);
    }

    /// <summary>
    ///     Relay up; destination listening on QUIC and through the
    ///     relay; dialer listening on QUIC and connected to the
    ///     destination over the relayed connection.
    /// </summary>
    private sealed record Topology(
        IManagedPeerSwitch RelaySwitch, IListener RelayListener,
        (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer) Destination,
        IListener DestinationQuicListener, IListener DestinationCircuitListener,
        (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer) Dialer,
        IListener DialerListener,
        IRemotePeerConnection DialerRelayed, IRemotePeerConnection DestinationRelayed);

    private static async Task<Topology> BuildTopologyAsync(CancellationToken token)
    {
        var (relaySwitch, relayPeer) = BuildRelayNode();
        IListener relayListener = await relaySwitch.ListenAsync(relayPeer, token);

        var destination = BuildNode();
        IListener destinationQuic = await destination.Switch.ListenAsync(destination.Peer, token);
        // The same peer also listens through the relay: its primary
        // address becomes the circuit one (the bound QUIC form stays
        // in its book — that is what its CONNECT will offer).
        destination.Peer.Address = Multiaddress.Decode($"{relayListener.Address}/p2p-circuit");
        IListener destinationCircuit = await destination.Switch.ListenAsync(destination.Peer, token);

        var dialer = BuildNode();
        IListener dialerListener = await dialer.Switch.ListenAsync(dialer.Peer, token);

        IRemotePeerConnection dialerRelayed =
            await dialer.Switch.DialAsync(destinationCircuit.Address, token);
        Assert.That(dialerRelayed.ParentConnection, Is.Not.Null,
            "The dialer's connection to the destination should be the relayed one.");

        IRemotePeerConnection destinationRelayed = await WaitForAsync(
            () => destination.Switch.LiveConnections.Keys.FirstOrDefault(c =>
                c.ParentConnection is not null
                && SamePeer(c, dialer.Peer.Identity)),
            TimeSpan.FromSeconds(15), token);
        Assert.That(destinationRelayed, Is.Not.Null,
            "The destination should hold the relayed connection from the dialer.");

        return new Topology(
            relaySwitch, relayListener, destination, destinationQuic, destinationCircuit,
            dialer, dialerListener, dialerRelayed, destinationRelayed!);
    }

    private static async Task<T?> WaitForAsync<T>(
        Func<T?> probe, TimeSpan timeout, CancellationToken token) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            if (probe() is { } found)
            {
                return found;
            }
            try
            {
                await Task.Delay(50, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    private static bool SamePeer(IRemotePeerConnection connection, Identity peer)
    {
        // Identity objects are per-node reconstructions of a key;
        // equality is by peer id, never by reference.
        string peerId = peer.PeerId.ToString();
        return connection.ProvenPeerId?.ToString() == peerId
            || connection.RemotePeer.Identity.PeerId.ToString() == peerId;
    }

    private static bool IsDirectQuicTo(IRemotePeerConnection connection, LocalPeer peer)
    {
        // A QUIC connection's endpoint in this stack is the bare
        // UDP socket form — /quic-v1 lives in the dialed address,
        // not in the established connection's endpoint — and a
        // simultaneous dial legitimately produces two direct
        // connections (our outbound one, endpoint = the peer's
        // listener; the peer's outbound one accepted here,
        // endpoint = its source socket). Either is the upgrade:
        // what defines it is a non-circuit UDP endpoint — QUIC is
        // the only UDP transport in this stack — to the proven
        // peer.
        string? endpoint = connection.RemoteEndpoint?.ToString();
        return endpoint is not null
            && endpoint.Contains("/udp/")
            && !endpoint.Contains("/p2p-circuit")
            && SamePeer(connection, peer.Identity);
    }

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported)
        {
            // Needs libmsquic and .NET IPv6 enabled. (The sandbox VM's
            // test runs set DOTNET_SYSTEM_NET_DISABLEIPV6=1 so vstest
            // can reach its testhost — which disables QUIC — so this
            // skips there and runs in CI.)
            Assert.Ignore("QUIC is not supported in this environment (libmsquic missing or IPv6 disabled).");
        }
    }

    [Test]
    public async Task HolePunch_OverRelayedConnection_UpgradesToDirectQuic()
    {
        RequireQuic();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Topology t = await BuildTopologyAsync(cts.Token);

        // The destination — the inbound side of the relayed
        // connection — initiates the upgrade, per the specification.
        await t.DestinationRelayed.DialAsync<DcutrProtocol>(cts.Token);

        IRemotePeerConnection? destinationDirect = await WaitForAsync(
            () => t.Destination.Switch.LiveConnections.Keys
                .FirstOrDefault(c => IsDirectQuicTo(c, t.Dialer.Peer)),
            TimeSpan.FromSeconds(15), cts.Token);
        Assert.That(destinationDirect, Is.Not.Null,
            "The destination should hold a direct QUIC connection to the dialer.");
        Assert.That(destinationDirect!.ProvenPeerId, Is.Not.Null);
        Assert.That(destinationDirect.ProvenPeerId!.ToString(),
            Is.EqualTo(t.Dialer.Peer.Identity.PeerId.ToString()));

        IRemotePeerConnection? dialerDirect = await WaitForAsync(
            () => t.Dialer.Switch.LiveConnections.Keys
                .FirstOrDefault(c => IsDirectQuicTo(c, t.Destination.Peer)),
            TimeSpan.FromSeconds(15), cts.Token);
        Assert.That(dialerDirect, Is.Not.Null,
            "The dialer should hold the other end of the direct QUIC connection.");

        // The upgrade is an addition, not a replacement: the
        // relayed connection still carries traffic.
        await t.DialerRelayed.DialAsync<PingProtocol>(cts.Token);
        await CleanupAsync(t);
    }

    [Test]
    public async Task HolePunchService_UpgradesRelayedConnection()
    {
        RequireQuic();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Topology t = await BuildTopologyAsync(cts.Token);

        var service = new HolePunchService(t.Destination.Switch);
        IRemotePeerConnection? direct = await service.TryUpgradeAsync(t.DestinationRelayed, cts.Token);

        Assert.That(direct, Is.Not.Null,
            "The service should produce a direct connection.");
        Assert.That(IsDirectQuicTo(direct!, t.Dialer.Peer), Is.True,
            "The service's result should be the direct QUIC connection to the dialer.");
        Assert.That(direct!.ProvenPeerId!.ToString(),
            Is.EqualTo(t.Dialer.Peer.Identity.PeerId.ToString()));

        await CleanupAsync(t);
    }

    private static async Task CleanupAsync(Topology t)
    {
        await t.DialerRelayed.DisconnectAsync();
        await t.DialerListener.DisconnectAsync();
        await t.DestinationCircuitListener.DisconnectAsync();
        await t.DestinationQuicListener.DisconnectAsync();
        await t.RelayListener.DisconnectAsync();
    }
}
