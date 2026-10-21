// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Ipfs.Kad.Tests;

/// <summary>
///     A peer's address book also holds what its Identify advertised,
///     and a peer with a relay reservation advertises /p2p-circuit
///     addresses — which carry TWO /p2p parts. Kad must still be
///     able to query such a peer: the job claim reads each address's
///     terminal /p2p (the peer the address names), and no address
///     in the book may throw while being read. Regression: the claim
///     used Multiaddress.Get&lt;P2P&gt; (SingleOrDefault), which
///     throws on a two-/p2p address — whenever one enumerated first,
///     the claim died before touching the job, and the query sat
///     orphaned until the client's deadline.
/// </summary>
[TestFixture]
[NonParallelizable]
public class KadCircuitAddressTests
{
    private sealed record Node(
        ServiceProvider Services,
        IManagedPeerSwitch Switch,
        LocalPeer Peer,
        KadClient Client,
        IListener Listener)
    {
        public KadPeer AsKadPeer => new(Peer.Identity.PeerId.Bytes, [Listener.Address]);
    }

    private static async Task<Node> BuildNodeAsync()
    {
        var localPeer = new LocalPeer(new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/tcp/0");

        ServiceProvider services = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder.AddAppLayerProtocol<KadDhtProtocol>())
            .AddKadDht(options => options.BootstrapPeers = null)
            .AddLogging()
            .BuildServiceProvider();

        IManagedPeerSwitch peerSwitch = services.GetRequiredService<IManagedPeerSwitch>();
        IListener listener = await peerSwitch.ListenAsync(localPeer);
        return new Node(services, peerSwitch, localPeer,
            services.GetRequiredService<KadClient>(), listener);
    }

    [Test]
    public async Task Query_Completes_WhenThePeersBookCarriesACircuitAddress()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node dialer = await BuildNodeAsync();
        Node responder = await BuildNodeAsync();
        dialer.Client.AddPeer(responder.AsKadPeer);

        // The circuit address the responder's Identify would carry
        // if it held a relay reservation: the responder reached
        // through a relay (the responder plays the relay too — only
        // the two /p2p parts matter). DialAddresses must leave such
        // an address untouched and must not throw on it.
        string responderId = responder.Peer.Identity.PeerId.ToString();
        Multiaddress circuit = Multiaddress.Decode(
            $"/ip4/127.0.0.1/tcp/4001/p2p/{responderId}/p2p-circuit/p2p/{responderId}");
        var circuitPeer = new KadPeer(responder.Peer.Identity.PeerId.Bytes, [circuit]);
        Assert.That(circuitPeer.DialAddresses, Is.EqualTo(new[] { circuit }));

        // Establish the connection so the responder is interned in
        // the dialer's switch, then put the circuit address in its
        // book — the state the dialer is really in after Identify
        // absorbs a relayed peer's advertised addresses.
        IRemotePeerConnection connection =
            await dialer.Switch.DialAsync(responder.Listener.Address, cts.Token);
        connection.RemotePeer.Addresses.TryAdd(circuit, new PeerAddressAccounting());

        // The query must complete: before the fix, the claim threw
        // on the circuit address and this await ran out the query
        // deadline instead of returning the responder's answer.
        var findNode = new Message
        {
            type = MessageType.FindNode,
            key = SHA256.HashData("a key the responder does not know"u8.ToArray()),
        };
        Message response = await dialer.Client.QueryAsync(responder.AsKadPeer, findNode, cts.Token);
        Assert.That(response, Is.Not.Null);
    }
}
