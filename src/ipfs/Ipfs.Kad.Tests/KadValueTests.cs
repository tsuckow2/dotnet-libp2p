// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Ipfs.Kad.Tests;

/// <summary>
///     DHT value records, over real loopback stacks with the
///     production wiring: a node publishes a value to the closest
///     peers, the record lands on them, and a lookup surfaces it.
///     A value is stored only on its own proof — a namespace with
///     no validator, or a validator that rejects the bytes, keeps
///     the record out.
/// </summary>
[TestFixture]
[NonParallelizable]
public class KadValueTests
{
    private sealed class TestValidator : IDhtValueValidator
    {
        public string Namespace => "/test/";

        public bool Reject { get; set; }

        public bool TryValidate(byte[] key, byte[] value, out DateTimeOffset expires, out string? error)
        {
            expires = DateTimeOffset.UtcNow.AddHours(1);
            if (Reject)
            {
                error = "test rejection";
                return false;
            }

            error = null;
            return true;
        }
    }

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

    private static async Task<Node> BuildNodeAsync(bool withValidator = true)
    {
        var localPeer = new LocalPeer(new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/tcp/0");

        var collection = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder.AddAppLayerProtocol<KadDhtProtocol>())
            .AddKadDht(options => options.BootstrapPeers = null)
            .AddLogging();
        if (withValidator)
        {
            collection.AddSingleton<IDhtValueValidator, TestValidator>();
        }

        ServiceProvider services = collection.BuildServiceProvider();

        IManagedPeerSwitch peerSwitch = services.GetRequiredService<IManagedPeerSwitch>();
        IListener listener = await peerSwitch.ListenAsync(localPeer);
        return new Node(services, peerSwitch, localPeer,
            services.GetRequiredService<KadClient>(),
            services.GetRequiredService<IKadPeerTable>(), listener);
    }

    private static readonly byte[] Key = Encoding.ASCII.GetBytes("/test/some-key");
    private static readonly byte[] Value = Encoding.UTF8.GetBytes("a value the network keeps");

    [Test]
    public async Task PutValue_AnnouncesToTheClosestPeers_AndGetValueSurfacesIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node publisher = await BuildNodeAsync();
        Node holder = await BuildNodeAsync();
        publisher.Client.AddPeer(holder.AsKadPeer);
        holder.Client.AddPeer(publisher.AsKadPeer);

        await publisher.Client.PutValueAsync(Key, Value, cts.Token);

        // The publisher's own record: kept locally, no network needed.
        Assert.That(publisher.Table.GetValue(Key)?.Value, Is.EqualTo(Value));

        // The announcement landed on the closest peer.
        Assert.That(holder.Table.GetValue(Key)?.Value, Is.EqualTo(Value));

        // And a lookup surfaces the value.
        IReadOnlyList<byte[]> found = await holder.Client.GetValueAsync(Key, cts.Token);
        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0], Is.EqualTo(Value));
    }

    [Test]
    public async Task GetValue_WhenNothingIsHeld_ReturnsEmpty()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node asker = await BuildNodeAsync();
        Node other = await BuildNodeAsync();
        asker.Client.AddPeer(other.AsKadPeer);
        other.Client.AddPeer(asker.AsKadPeer);

        IReadOnlyList<byte[]> found = await asker.Client.GetValueAsync(Key, cts.Token);
        Assert.That(found, Is.Empty);
    }

    [Test]
    public async Task PutValue_WithoutValidator_IsRejectedOnBothSides()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node noValidator = await BuildNodeAsync(withValidator: false);

        // The publish path faults: nothing validates this namespace.
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await noValidator.Client.PutValueAsync(Key, Value, cts.Token));

        // And a raw PUT_VALUE sent at the node is dropped, never stored.
        Node announcer = await BuildNodeAsync();
        var put = new Message
        {
            type = MessageType.PutValue,
            key = Key,
            record = new Record { key = Key, value = Value, timeReceived = KadClient.NowStamp() },
        };
        await announcer.Client.QueryAsync(noValidator.AsKadPeer, put, cts.Token);
        Assert.That(noValidator.Table.GetValue(Key), Is.Null);
    }

    [Test]
    public async Task PutValue_FailingValidation_IsNotStored()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Node publisher = await BuildNodeAsync();
        Node holder = await BuildNodeAsync();
        publisher.Client.AddPeer(holder.AsKadPeer);
        holder.Client.AddPeer(publisher.AsKadPeer);

        // The holder's validator rejects everything now: the
        // announcement must not land.
        var services = holder.Services;
        TestValidator validator = services.GetServices<IDhtValueValidator>()
            .OfType<TestValidator>().Single();
        validator.Reject = true;

        await publisher.Client.PutValueAsync(Key, Value, cts.Token);
        Assert.That(holder.Table.GetValue(Key), Is.Null);
    }

    [Test]
    public void ValueStore_ExpiredRecordsAreNotServed()
    {
        var store = new ValueStore();
        store.Put(Key, Value, DateTimeOffset.UtcNow.AddSeconds(-1), KadClient.NowStamp());
        Assert.That(store.Get(Key), Is.Null);

        store.Put(Key, Value, DateTimeOffset.UtcNow.AddHours(1), KadClient.NowStamp());
        Assert.That(store.Get(Key)?.Value, Is.EqualTo(Value));
        Assert.That(store.Snapshot(), Has.Count.EqualTo(1));
    }

    [Test]
    public void ValueNamespace_ParsesTheKeyPrefix()
    {
        Assert.That(DhtValueNamespace.For(Encoding.ASCII.GetBytes("/ipns/abc")), Is.EqualTo("/ipns/"));
        Assert.That(DhtValueNamespace.For("garbage"u8.ToArray()), Is.Null);
        Assert.That(DhtValueNamespace.For("/x"u8.ToArray()), Is.Null);
    }
}
