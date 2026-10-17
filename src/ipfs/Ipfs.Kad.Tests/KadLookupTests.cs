// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Extensions;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Ipfs.Kad.Tests;

/// <summary>
///     The Kad client over real loopback stacks: nodes answer from
///     static tables, and the client's iterative walk must absorb
///     closer peers across hops and surface the providers they name.
/// </summary>
[TestFixture]
[NonParallelizable]
public class KadLookupTests
{
    private sealed class StaticTable : IKadPeerTable
    {
        public Func<byte[], IReadOnlyList<KadPeer>> Closer { get; set; } = _ => [];
        public Func<byte[], IReadOnlyList<KadPeer>> Providers { get; set; } = _ => [];

        public IReadOnlyList<KadPeer> GetCloserPeers(byte[] target, int count) => Closer(target);
        public IReadOnlyList<KadPeer> GetProviders(byte[] key) => Providers(key);
    }

    private sealed record Node(
        ServiceProvider Services,
        IManagedPeerSwitch Switch,
        LocalPeer Peer,
        KadClient Client,
        StaticTable Table,
        IListener Listener)
    {
        public KadPeer AsKadPeer => new(Peer.Identity.PeerId.Bytes, [Listener.Address]);
    }

    private static async Task<Node> BuildNodeAsync(KeyType keyType = KeyType.Ed25519, string? bootstrap = null)
    {
        var localPeer = new LocalPeer(new Identity(keyType: keyType));
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/tcp/0");
        var table = new StaticTable();

        var collection = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder.AddAppLayerProtocol<KadDhtProtocol>())
            .AddSingleton<IKadPeerTable>(table)
            .AddLogging();
        // With a bootstrap entry the node registers the client the
        // wired-up way (AddKadDht); without, bare — unseeded until
        // told otherwise, as the other tests rely on.
        if (bootstrap is null)
        {
            collection.AddSingleton<KadClient>();
        }
        else
        {
            collection.AddKadDht(options => options.BootstrapPeers = [bootstrap]);
        }
        ServiceProvider services = collection.BuildServiceProvider();

        IManagedPeerSwitch peerSwitch = services.GetRequiredService<IManagedPeerSwitch>();
        IListener listener = await peerSwitch.ListenAsync(localPeer);
        return new Node(services, peerSwitch, localPeer,
            services.GetRequiredService<KadClient>(), table, listener);
    }

    private static readonly byte[] Key = SHA256.HashData("a block nobody has"u8.ToArray());

    [Test]
    public async Task Startup_SeedsTheConfiguredBootstrapPeers()
    {
        Node provider = await BuildNodeAsync();
        string bootstrap = provider.AsKadPeer.DialAddresses.First().ToString();
        Node client = await BuildNodeAsync(bootstrap: bootstrap);

        Assert.That(client.Client.KnownPeers, Is.Empty,
            "registration alone must not seed — the startup pass does");

        await client.Services.StartLibp2pAsync();

        Assert.That(client.Client.KnownPeers.Select(peer => peer.Id),
            Does.Contain(provider.Peer.Identity.PeerId.Bytes));
    }

    [Test]
    public async Task FirstLookup_SeedsLazily_WhenNoStartupPassRan()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node provider = await BuildNodeAsync();
        string bootstrap = provider.AsKadPeer.DialAddresses.First().ToString();
        Node client = await BuildNodeAsync(bootstrap: bootstrap);

        provider.Table.Providers = _ => [provider.AsKadPeer];

        // No StartLibp2pAsync, no AddPeer: the lookup itself must
        // begin (and await) the configured seed, or it walks an
        // empty book and finds nothing.
        IReadOnlyList<KadPeer> found = await client.Client.FindProvidersAsync(Key, cts.Token);

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Id, Is.EqualTo(provider.Peer.Identity.PeerId.Bytes));
    }

    [Test]
    public async Task FindProviders_DirectAnswer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node provider = await BuildNodeAsync();
        Node client = await BuildNodeAsync();

        provider.Table.Providers = _ => [provider.AsKadPeer];
        client.Client.AddPeer(provider.AsKadPeer);

        IReadOnlyList<KadPeer> found = await client.Client.FindProvidersAsync(Key, cts.Token);

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Id, Is.EqualTo(provider.Peer.Identity.PeerId.Bytes));
    }

    [Test]
    public async Task FindProviders_FromRsaPeer()
    {
        // An RSA peer's id is a hash of its key: the dialed
        // connection's remote identity cannot be seeded from the
        // address, and the query job must still be claimed (the
        // public network's bootstrap peers are RSA peers).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node provider = await BuildNodeAsync(KeyType.Rsa);
        Node client = await BuildNodeAsync();

        provider.Table.Providers = _ => [provider.AsKadPeer];
        client.Client.AddPeer(provider.AsKadPeer);

        IReadOnlyList<KadPeer> found = await client.Client.FindProvidersAsync(Key, cts.Token);

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Id, Is.EqualTo(provider.Peer.Identity.PeerId.Bytes));
    }

    [Test]
    public async Task FindProviders_WalksCloserPeers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node provider = await BuildNodeAsync();
        Node middle = await BuildNodeAsync();
        Node client = await BuildNodeAsync();

        // The middle node names nobody for the key but points closer
        // (at the provider); only the provider's table has the record.
        middle.Table.Closer = _ => [provider.AsKadPeer];
        provider.Table.Providers = _ => [provider.AsKadPeer];
        client.Client.AddPeer(middle.AsKadPeer);

        IReadOnlyList<KadPeer> found = await client.Client.FindProvidersAsync(Key, cts.Token);

        Assert.That(found.Select(peer => peer.Id), Has.Member(provider.Peer.Identity.PeerId.Bytes));
        Assert.That(client.Client.KnownPeers.Select(peer => peer.Id),
            Has.Member(provider.Peer.Identity.PeerId.Bytes));
    }
}
