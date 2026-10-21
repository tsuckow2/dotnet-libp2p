// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Ipfs.Kad.Tests;

/// <summary>
///     Providing, over real loopback stacks with the production
///     wiring (AddKadDht's own peer table and provider store):
///     a node announces a block to the closest peers, the record
///     lands on them, and a lookup surfaces the provider. Records
///     naming a third party are not stored — a peer announces
///     only itself.
/// </summary>
[TestFixture]
[NonParallelizable]
public class KadProvideTests
{
    private sealed record Node(
        ServiceProvider Services,
        IManagedPeerSwitch Switch,
        LocalPeer Peer,
        KadClient Client,
        IKadPeerTable Table,
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
            services.GetRequiredService<KadClient>(),
            services.GetRequiredService<IKadPeerTable>(), listener);
    }

    private static readonly byte[] Key = SHA256.HashData("a block the provider has"u8.ToArray());

    [Test]
    public async Task Provide_AnnouncesToTheClosestPeers_AndFindProvidersSurfacesTheProvider()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node provider = await BuildNodeAsync();
        Node finder = await BuildNodeAsync();
        provider.Client.AddPeer(finder.AsKadPeer);
        finder.Client.AddPeer(provider.AsKadPeer);

        await provider.Client.ProvideAsync(Key, cts.Token);

        // The provider's own record: kept locally, no network needed.
        Assert.That(provider.Table.GetProviders(Key).Select(peer => peer.Id),
            Does.Contain(provider.Peer.Identity.PeerId.Bytes));

        // The announcement landed on the closest peer.
        Assert.That(finder.Table.GetProviders(Key).Select(peer => peer.Id),
            Does.Contain(provider.Peer.Identity.PeerId.Bytes));

        // And a lookup surfaces the provider.
        IReadOnlyList<KadPeer> found = await finder.Client.FindProvidersAsync(Key, cts.Token);
        Assert.That(found.Select(peer => peer.Id),
            Does.Contain(provider.Peer.Identity.PeerId.Bytes));
    }

    [Test]
    public async Task AddProvider_NamingAThirdParty_IsNotStored()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node announcer = await BuildNodeAsync();
        Node recorder = await BuildNodeAsync();
        Node thirdParty = await BuildNodeAsync();

        // A record naming someone else: the recorder must ignore
        // it — the connection's peer is the announcer, and nobody
        // provides on behalf of a third party.
        var forged = new Message
        {
            type = MessageType.AddProvider,
            key = Key,
            providerPeers = [thirdParty.AsKadPeer.ToWire()],
        };
        await announcer.Client.QueryAsync(recorder.AsKadPeer, forged, cts.Token);
        Assert.That(recorder.Table.GetProviders(Key), Is.Empty);

        // The announcer's own record, by contrast, is stored.
        var honest = new Message
        {
            type = MessageType.AddProvider,
            key = Key,
            providerPeers = [announcer.AsKadPeer.ToWire()],
        };
        await announcer.Client.QueryAsync(recorder.AsKadPeer, honest, cts.Token);
        Assert.That(recorder.Table.GetProviders(Key).Select(peer => peer.Id),
            Does.Contain(announcer.Peer.Identity.PeerId.Bytes));
    }
}
