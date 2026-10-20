// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
///     The Phase 3 acceptance test: two switches over real TCP sockets.
///     The dialer connects through the full registry-assembled stack
///     (TCP, multistream, Noise, multistream, Yamux, multistream), then
///     uses both a channel-tier protocol (Ping) and a managed-tier
///     protocol (Echo, via the adapter) over the one connection.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SwitchEndToEndTests
{
    private class EchoProtocol : SymmetricProtocol<string>
    {
        public static ProtocolRef<EchoProtocol, string> Ref => new();

        public override string Id => "/echo/1.0.0";

        protected override async Task<IManagedProtocolComm<string>> ConnectAsync(IManagedRemoteComm comm, bool isListener)
        {
            if (isListener)
            {
                _ = Task.Run(async () =>
                {
                    string line = await comm.Channel.ReadLineAsync();
                    await comm.Channel.WriteLineAsync(line);
                });
                return new ManagedProtocolComm<string>("listener", comm);
            }

            await comm.Channel.WriteLineAsync("hello");
            string echo = await comm.Channel.ReadLineAsync();
            return new ManagedProtocolComm<string>(echo, comm);
        }
    }

    private static (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer) BuildNode(Identity? identity = null)
    {
        var localPeer = new LocalPeer(identity ?? new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/tcp/0");

        ServiceProvider services = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder.AddManagedProtocol(EchoProtocol.Ref))
            .AddLogging()
            .BuildServiceProvider();

        return (services, services.GetRequiredService<IManagedPeerSwitch>(), localPeer);
    }

    [Test]
    public async Task TwoNodes_Dial_Ping_AndManagedEcho()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);
        Assert.That(listener.Address, Is.Not.Null);

        IRemotePeerConnection connection = await dialerNode.Switch.DialAsync(listener.Address, cts.Token);
        Assert.That(connection, Is.Not.Null);

        // Channel tier: Ping over the connection's stack.
        await connection.DialAsync<PingProtocol>(cts.Token);

        // Managed tier: Echo through the adapter.
        IManagedProtocolComm<string> echo = await connection.Managed.DialAsync(EchoProtocol.Ref, cts.Token);
        Assert.That(echo.comm, Is.EqualTo("hello"));

        await connection.DisconnectAsync();
        await listener.DisconnectAsync();
    }

    [Test]
    public async Task DialPeer_DeadAddressFirst_RecordsFailure_ThenSkipsIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);

        IRemotePeer peer = dialerNode.Switch.GetRemotePeer(listenerNode.Peer.Identity);
        Multiaddress deadAddress = "/ip4/127.0.0.1/tcp/1";
        peer.GetOrAddAddress(deadAddress);

        // Only the dead address is known: the dial fails and is accounted.
        Assert.ThrowsAsync<PeerConnectionException>(async () => await dialerNode.Switch.DialAsync(peer, cts.Token));
        Assert.That(peer.Addresses[deadAddress].FailedDials, Is.EqualTo(1));

        // With a live address added, the dial succeeds — and tries the
        // live one first (fewest failures), leaving the count at 1.
        peer.GetOrAddAddress(listener.Address);
        IRemotePeerConnection connection = await dialerNode.Switch.DialAsync(peer, cts.Token);
        Assert.That(connection, Is.Not.Null);
        Assert.That(peer.Addresses[deadAddress].FailedDials, Is.EqualTo(1));

        await connection.DisconnectAsync();
        await listener.DisconnectAsync();
    }

    [Test]
    public async Task TwoDialsToTheSamePeer_ShareOneRemotePeer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);

        // The address names the peer by an identity-hash id, so both
        // dials must land on the switch's one interned RemotePeer for
        // that identity — not a fresh object per dial.
        IRemotePeerConnection first = await dialerNode.Switch.DialAsync(listener.Address, cts.Token);
        IRemotePeerConnection second = await dialerNode.Switch.DialAsync(listener.Address, cts.Token);

        Assert.That(second.RemotePeer, Is.SameAs(first.RemotePeer));
        Assert.That(first.RemotePeer.Identity, Is.EqualTo(listenerNode.Peer.Identity));

        await first.DisconnectAsync();
        await second.DisconnectAsync();
        await listener.DisconnectAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        // Poll briefly for an asynchronous effect (a fold-in landing
        // just after a dial returns) instead of sleeping a fixed span.
        for (int i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100, token);
        }
        Assert.That(condition(), Is.True, "The condition was not met within 10 s.");
    }

    [Test]
    public async Task DialWithoutPeerId_SecurityNamesThePeer_AndItBecomesCanonical()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // An RSA listener: its peer id is a hash of the key, so even a
        // /p2p-carrying address could not seed the identity — and this
        // dial does not carry one at all. The peer starts anonymous,
        // but the security handshake proves the listener's key before
        // the dial completes, so the connection is already named —
        // and already the canonical peer — when DialAsync returns.
        // (Identify used to do this naming, after the fact.)
        var listenerNode = BuildNode(new Identity(keyType: KeyType.Rsa));
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);
        string full = listener.Address.ToString();
        Multiaddress bareAddress = full[..full.IndexOf("/p2p/", StringComparison.Ordinal)];

        IRemotePeerConnection connection = await dialerNode.Switch.DialAsync(bareAddress, cts.Token);
        Assert.That(connection.RemotePeer.Identity, Is.EqualTo(listenerNode.Peer.Identity));
        Assert.That(
            ((ManagedPeerSwitch)dialerNode.Switch).RemotePeers.TryGetValue(listenerNode.Peer.Identity, out IRemotePeer? canonical)
                && ReferenceEquals(canonical, connection.RemotePeer),
            Is.True);

        await connection.DisconnectAsync();
        await listener.DisconnectAsync();
    }

    [Test]
    public async Task AnonymousDial_AfterIdentifiedDial_MergesIntoTheCanonicalPeer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode(new Identity(keyType: KeyType.Rsa));
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);
        string full = listener.Address.ToString();
        Multiaddress bareAddress = full[..full.IndexOf("/p2p/", StringComparison.Ordinal)];

        // First contact WITH the /p2p address still starts anonymous
        // for an RSA peer (hashed id), but a second, bare dial must
        // not split the peer: whichever object the security handshake
        // names first becomes canonical, and the other connection
        // folds into it.
        IRemotePeerConnection first = await dialerNode.Switch.DialAsync(listener.Address, cts.Token);
        await WaitForAsync(
            () => first.RemotePeer.Identity.Equals(listenerNode.Peer.Identity),
            cts.Token);
        IRemotePeer canonical = first.RemotePeer;
        await first.DisconnectAsync();

        IRemotePeerConnection second = await dialerNode.Switch.DialAsync(bareAddress, cts.Token);
        await WaitForAsync(
            () => ReferenceEquals(second.RemotePeer, canonical),
            cts.Token);

        // The bare address, known only to the second (folded) peer
        // object, is in the canonical peer's address book now.
        Assert.That(canonical.Addresses.ContainsKey(bareAddress), Is.True);

        await second.DisconnectAsync();
        await listener.DisconnectAsync();
    }

    [Test]
    public async Task Dial_CircuitAddress_RefusedUntilACircuitTransportIsRegistered()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();
        var destination = new Identity();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);

        // A routed address: the listening node plays the relay (its
        // endpoint and identity are the address's prefix); the
        // destination exists only as the address's terminal /p2p.
        var circuitAddress = Multiaddress.Decode(
            $"{listener.Address}/p2p-circuit/p2p/{destination.PeerId}");

        // No circuit transport is registered, so the switch refuses at
        // the address's outermost boundary instead of socket-dialing
        // the relay's endpoint as if it were the destination's.
        NotSupportedException? error = Assert.ThrowsAsync<NotSupportedException>(
            async () => await dialerNode.Switch.DialAsync(circuitAddress, cts.Token));
        Assert.That(error!.Message, Does.Contain("p2p-circuit"));

        // The peer the dial seeded from the address is the destination
        // (the terminal /p2p), never the relay.
        var dialer = (ManagedPeerSwitch)dialerNode.Switch;
        Assert.That(dialer.RemotePeers.ContainsKey(destination), Is.True);
        Assert.That(dialer.RemotePeers.ContainsKey(listenerNode.Peer.Identity), Is.False);

        // And nothing ever connected to the "relay".
        Assert.That(((ManagedPeerSwitch)listenerNode.Switch).RemotePeers.IsEmpty, Is.True);

        await listener.DisconnectAsync();
    }

    [Test]
    public async Task Dial_WebSocketAddress_RefusedUntilAWsTransportIsRegistered()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);

        // The listener's own endpoint with /ws layered over it: the
        // outermost transport component is now ws, which no registered
        // transport declares — IpTcp must not claim the address just
        // because it contains its /tcp/ endpoint underneath.
        string full = listener.Address.ToString();
        var wsAddress = Multiaddress.Decode(
            full[..full.IndexOf("/p2p/", StringComparison.Ordinal)] + "/ws"
            + full[full.IndexOf("/p2p/", StringComparison.Ordinal)..]);

        NotSupportedException? error = Assert.ThrowsAsync<NotSupportedException>(
            async () => await dialerNode.Switch.DialAsync(wsAddress, cts.Token));
        Assert.That(error!.Message, Does.Contain("'ws'"));
        Assert.That(((ManagedPeerSwitch)listenerNode.Switch).RemotePeers.IsEmpty, Is.True);

        await listener.DisconnectAsync();
    }

    [Test]
    public void Dial_BareUdpAddress_RefusedBecauseNoTransportDeclaresUdp()
    {
        var dialerNode = BuildNode();

        // A bare /udp/ address: udp is a transport component, but the
        // Quic transport declares quic-v1, not udp — there is nothing
        // to serve this address with.
        var udpAddress = Multiaddress.Decode("/ip4/127.0.0.1/udp/4001");

        NotSupportedException? error = Assert.ThrowsAsync<NotSupportedException>(
            async () => await dialerNode.Switch.DialAsync(udpAddress, CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("'udp'"));
    }

    [Test]
    public async Task Dial_ImpostorAtVictimAddress_FaultsAtTheHandshake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();
        var victim = new Identity();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);

        // The listener's endpoint, the victim's /p2p: whoever answers
        // holds the listener's key, and the Noise handshake proves a
        // different peer than the address named — the dial must fault
        // there, not hand out a connection to the wrong peer.
        var victimAddress = AddressWithPeer(listener.Address, victim);

        Assert.ThrowsAsync<PeerConnectionException>(
            async () => await dialerNode.Switch.DialAsync(victimAddress, cts.Token));

        await listener.DisconnectAsync();
    }

    [Test]
    public async Task Dial_ImpostorAtRsaVictimAddress_FaultsAtTheHandshake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();
        // A hashed peer id: the key cannot be extracted from it, but
        // the proven key can be hashed and compared — the case the
        // old trust-on-dial caveat carved out.
        var victim = new Identity(keyType: KeyType.Rsa);

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);

        var victimAddress = AddressWithPeer(listener.Address, victim);

        Assert.ThrowsAsync<PeerConnectionException>(
            async () => await dialerNode.Switch.DialAsync(victimAddress, cts.Token));

        await listener.DisconnectAsync();
    }

    private static Multiaddress AddressWithPeer(Multiaddress address, Identity peer)
    {
        string full = address.ToString();
        return Multiaddress.Decode(
            full[..full.IndexOf("/p2p/", StringComparison.Ordinal)] + $"/p2p/{peer.PeerId}");
    }
}
