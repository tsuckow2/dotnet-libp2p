// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
///     Pubsub over the switch: two nodes, each running a PubsubRouter on
///     the bridged peer surface (LocalPeer.ListenAsync/DialAsync,
///     RemotePeer.DialAsync for the gossipsub dial). The publisher's
///     router discovers the subscriber through a static discovery
///     double, dials it through the switch, and a published message
///     must arrive at the subscriber's topic. This is the acceptance
///     test for the bridge the router still stands on.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PubsubEndToEndTests
{
    private class StaticDiscovery(Multiaddress? peerAddress) : IDiscoveryProtocol
    {
        public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
        public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

        public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
        {
            if (peerAddress is not null)
            {
                OnAddPeer?.Invoke([peerAddress]);
            }
            return Task.Delay(Timeout.Infinite, token);
        }
    }

    private static (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer, PubsubRouter Router) BuildNode()
    {
        var identity = new Identity();
        var localPeer = new LocalPeer(identity)
        {
            Address = $"/ip4/127.0.0.1/tcp/0/p2p/{identity.PeerId}",
        };

        ServiceProvider services = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder)
            .AddLogging()
            .BuildServiceProvider();

        return (services,
            services.GetRequiredService<IManagedPeerSwitch>(),
            localPeer,
            services.GetRequiredService<PubsubRouter>());
    }

    [Test]
    public async Task TwoNodes_Gossipsub_MessageDelivered()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var settings = new Settings { HeartbeatInterval = 200 };

        var subscriber = BuildNode();
        var publisher = BuildNode();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ITopic subscriberTopic = subscriber.Router.Subscribe("test-topic");
        subscriberTopic.OnMessage += msg => received.TrySetResult(msg);
        ITopic publisherTopic = publisher.Router.Subscribe("test-topic");

        _ = subscriber.Router.RunAsync(subscriber.Peer, new StaticDiscovery(null), settings, cts.Token);

        // The router listens through the bridge; wait for the bound port.
        while (subscriber.Peer.Address.Get<TCP>().Port == 0)
        {
            await Task.Delay(50, cts.Token);
        }

        _ = publisher.Router.RunAsync(publisher.Peer, new StaticDiscovery(subscriber.Peer.Address), settings, cts.Token);

        // Pubsub gives no signal for "the mesh has grafted", and a
        // message published before grafting is simply not forwarded —
        // so publish until delivery or the window closes. What is being
        // tested is delivery over the switch, not graft timing.
        byte[] payload = Encoding.UTF8.GetBytes("hello pubsub");
        using var publishWindow = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!received.Task.IsCompleted && !publishWindow.IsCancellationRequested)
        {
            publisherTopic.Publish(payload);
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        }

        byte[] message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.That(message, Is.EqualTo(payload));
    }
}
