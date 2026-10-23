// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Dcutr.Tests;

[TestFixture]
[NonParallelizable]
public class DcutrProtocolTests
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     A switch double whose dials all fail (the NAT wins) and
    ///     which holds no connections, recording every dial target
    ///     so the test can see exactly what the punch aimed at.
    /// </summary>
    private static IManagedPeerSwitch FailingSwitch(ConcurrentQueue<string> dialLog)
    {
        var @switch = Substitute.For<IManagedPeerSwitch>();
        @switch.LiveConnections.Returns(new Dictionary<IRemotePeerConnection, DateTimeOffset>());
        @switch.DialAsync(
                Arg.Any<IRemotePeer>(), Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                dialLog.Enqueue(call.Arg<Multiaddress>().ToString());
                return Task.FromException<IRemotePeerConnection>(
                    new PeerConnectionException("Nothing answers there."));
            });
        return @switch;
    }

    private static DcutrProtocol ProtocolFor(IManagedPeerSwitch @switch)
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(@switch)
            .BuildServiceProvider();
        return new DcutrProtocol(services,
            new DcutrProtocolSettings { AttemptTimeout = AttemptTimeout });
    }

    private static LocalPeer NodeWithAddresses(params string[] addresses)
    {
        var local = new LocalPeer(new Identity());
        foreach (string address in addresses)
        {
            local.GetOrAddAddress(Multiaddress.Decode(address));
        }
        return local;
    }

    [Test]
    public async Task Exchange_BothSidesDialTheAddressesFromThePeersConnect()
    {
        var initiatorDials = new ConcurrentQueue<string>();
        var responderDials = new ConcurrentQueue<string>();

        LocalPeer initiatorNode = NodeWithAddresses(
            "/ip4/192.0.2.10/udp/4001/quic-v1", "/ip4/192.0.2.10/tcp/4001");
        LocalPeer responderNode = NodeWithAddresses(
            "/ip4/198.51.100.20/udp/5001/quic-v1", "/ip4/198.51.100.20/tcp/5001");

        DcutrProtocol initiator = ProtocolFor(FailingSwitch(initiatorDials));
        DcutrProtocol responder = ProtocolFor(FailingSwitch(responderDials));

        // The connections the exchange runs over: each side's view
        // of the relayed connection to the other, identity proven.
        var initiatorConnection = new TestRemotePeerConnection(
            new FakePeer(responderNode.Identity), initiatorNode)
        {
            ProvenPeerId = responderNode.Identity.PeerId,
        };
        var responderConnection = new TestRemotePeerConnection(
            new FakePeer(initiatorNode.Identity), responderNode)
        {
            ProvenPeerId = initiatorNode.Identity.PeerId,
        };

        var channel = new TestChannel();
        Task initiating = initiator.DialAsync(channel, null, initiatorConnection);
        Task answering = responder.ListenAsync(channel.Reverse(), null, responderConnection);

        // A failed punch is an outcome, not an exception: both
        // sides run their window out and return.
        await Task.WhenAll(initiating, answering).WaitAsync(TimeSpan.FromSeconds(30));

        // The responder dialed the initiator's addresses (from the
        // initiator's CONNECT) when SYNC arrived; the initiator
        // dialed the responder's (from its CONNECT) — QUIC first,
        // each named with the destination it must prove.
        Assert.That(responderDials.ToArray(), Is.EqualTo(new[]
        {
            $"/ip4/192.0.2.10/udp/4001/quic-v1/p2p/{initiatorNode.Identity.PeerId}",
            $"/ip4/192.0.2.10/tcp/4001/p2p/{initiatorNode.Identity.PeerId}",
        }));
        // (The initiator's dials both leave at half the measured
        // RTT, so their relative order is the timer's, not the
        // list's — the set and its /p2p naming are what matter.)
        Assert.That(initiatorDials.ToArray(), Is.EquivalentTo(new[]
        {
            $"/ip4/198.51.100.20/udp/5001/quic-v1/p2p/{responderNode.Identity.PeerId}",
            $"/ip4/198.51.100.20/tcp/5001/p2p/{responderNode.Identity.PeerId}",
        }));
    }

    [Test]
    public async Task Initiator_RefusesAReplyThatIsNotConnect()
    {
        LocalPeer initiatorNode = NodeWithAddresses("/ip4/192.0.2.10/udp/4001/quic-v1");
        DcutrProtocol initiator = ProtocolFor(FailingSwitch(new ConcurrentQueue<string>()));
        var connection = new TestRemotePeerConnection(
            new FakePeer(new Identity()), initiatorNode)
        {
            ProvenPeerId = new Identity().PeerId,
        };

        var channel = new TestChannel();
        Task initiating = initiator.DialAsync(channel, null, connection);

        // Consume the initiator's opening message (the rendezvous
        // channel lets no write complete unread), and check it
        // opened with a CONNECT as the protocol requires.
        HolePunchMessage opened = await HolePunchWire.ReadAsync(channel.Reverse());
        Assert.That(opened.Type, Is.EqualTo(HolePunchMessageType.Connect));

        // A SYNC where the responder's CONNECT belongs is a
        // protocol violation; the initiator must fault, not dial.
        await HolePunchWire.WriteAsync(channel.Reverse(),
            new HolePunchMessage { Type = HolePunchMessageType.Sync });

        Assert.ThrowsAsync<PeerConnectionException>(async () => await initiating);
    }

    [Test]
    public async Task Responder_RefusesAStreamThatDoesNotOpenWithConnect()
    {
        LocalPeer responderNode = NodeWithAddresses("/ip4/198.51.100.20/udp/5001/quic-v1");
        DcutrProtocol responder = ProtocolFor(FailingSwitch(new ConcurrentQueue<string>()));
        var connection = new TestRemotePeerConnection(
            new FakePeer(new Identity()), responderNode)
        {
            ProvenPeerId = new Identity().PeerId,
        };

        var channel = new TestChannel();
        Task answering = responder.ListenAsync(channel, null, connection);

        await HolePunchWire.WriteAsync(channel.Reverse(),
            new HolePunchMessage { Type = HolePunchMessageType.Sync });

        Assert.ThrowsAsync<PeerConnectionException>(async () => await answering);
    }
}
