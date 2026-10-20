// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net.Quic;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
///     The Quic acceptance test: two switches over a real QUIC
///     connection. Quic terminates the stack itself (its TLS is the
///     security layer, its streams are the muxer), so above it the
///     switch assembles only the selector and the app protocols.
///     Both tiers are exercised over the one connection, as in the
///     TCP fixture: Ping at the channel tier, Echo at the managed tier.
/// </summary>
[TestFixture]
[NonParallelizable]
public class QuicEndToEndTests
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

    private static (ServiceProvider Services, IManagedPeerSwitch Switch, LocalPeer Peer) BuildNode()
    {
        var localPeer = new LocalPeer(new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/udp/0/quic-v1");

        ServiceProvider services = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder.AddManagedProtocol(EchoProtocol.Ref))
            .AddLogging()
            .BuildServiceProvider();

        return (services, services.GetRequiredService<IManagedPeerSwitch>(), localPeer);
    }

    [Test]
    public async Task TwoNodes_Dial_Ping_AndManagedEcho_OverQuic()
    {
        if (!QuicListener.IsSupported)
        {
            // Needs libmsquic and .NET IPv6 enabled. (The sandbox VM's
            // test runs set DOTNET_SYSTEM_NET_DISABLEIPV6=1 so vstest can
            // reach its testhost — which disables QUIC — so this skips
            // there and runs in CI.)
            Assert.Ignore("QUIC is not supported in this environment (libmsquic missing or IPv6 disabled).");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);
        Assert.That(listener.Address, Is.Not.Null);
        Assert.That(listener.Address.ToString(), Does.Contain("/quic-v1"));

        IRemotePeerConnection connection = await dialerNode.Switch.DialAsync(listener.Address, cts.Token);
        Assert.That(connection, Is.Not.Null);

        // Channel tier: Ping over a QUIC stream.
        await connection.DialAsync<PingProtocol>(cts.Token);

        // Managed tier: Echo through the adapter.
        IManagedProtocolComm<string> echo = await connection.Managed.DialAsync(EchoProtocol.Ref, cts.Token);
        Assert.That(echo.comm, Is.EqualTo("hello"));

        await connection.DisconnectAsync();
        await listener.DisconnectAsync();
    }

    [Test]
    public async Task Listener_NamesInboundPeer_FromItsCertificate()
    {
        if (!QuicListener.IsSupported)
        {
            Assert.Ignore("QUIC is not supported in this environment (libmsquic missing or IPv6 disabled).");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listenerNode = BuildNode();
        var dialerNode = BuildNode();

        IListener listener = await listenerNode.Switch.ListenAsync(listenerNode.Peer, cts.Token);

        var named = new TaskCompletionSource<Identity>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnConnection += peer =>
        {
            // Raised as the connection is established, before any
            // identity protocol can have run on it (inbound streams
            // are only accepted afterwards). The name can only have
            // come from the handshake's proof.
            named.TrySetResult(peer.Identity);
            return Task.CompletedTask;
        };

        IRemotePeerConnection connection = await dialerNode.Switch.DialAsync(listener.Address, cts.Token);

        Identity namedByListener = await named.Task.WaitAsync(cts.Token);
        Assert.That(namedByListener, Is.EqualTo(dialerNode.Peer.Identity));

        // The connection works, and the dialer's Identify passes the
        // listener's cross-check against the proven identity.
        await connection.DialAsync<PingProtocol>(cts.Token);

        await connection.DisconnectAsync();
        await listener.DisconnectAsync();
    }
}
