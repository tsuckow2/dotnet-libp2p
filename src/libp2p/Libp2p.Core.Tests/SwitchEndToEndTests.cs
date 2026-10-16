// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
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

        protected override async Task NegotiateAsync(IManagedRemoteComm comm)
        {
            await NegotiationUtils.NegotiateProtocolAsync(comm, Id);
        }

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

    private static (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer) BuildNode()
    {
        var localPeer = new LocalPeer(new Identity());
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
}
