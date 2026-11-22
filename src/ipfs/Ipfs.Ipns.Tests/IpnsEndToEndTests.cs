// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Ipfs.Kad;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Extensions;
using Nethermind.Libp2p.Stack;
using NUnit.Framework;

namespace Nethermind.Ipfs.Ipns.Tests;

/// <summary>
///     IPNS over the DHT, in a closed network of real loopback
///     nodes with the production wiring: a publisher announces a
///     record, the record lands on the closest peers, and a cold
///     peer resolves it after the publisher is gone. The publisher
///     is a delegate — its node identity differs from the IPNS key —
///     exactly as the backup agent publishes for a config owner.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IpnsEndToEndTests
{
    private sealed record Node(
        ServiceProvider Services,
        IManagedPeerSwitch Switch,
        LocalPeer Peer,
        KadClient Client,
        IKadPeerTable Table,
        IpnsService Ipns,
        IListener Listener)
    {
        public KadPeer AsKadPeer => new(Peer.Identity.PeerId.Bytes, [Listener.Address]);
    }

    private static async Task<Node> BuildNodeAsync(Identity ipnsKey, TimeSpan? republishInterval = null)
    {
        var localPeer = new LocalPeer(new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/tcp/0");

        ServiceProvider services = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder.AddAppLayerProtocol<KadDhtProtocol>())
            .AddKadDht(options => options.BootstrapPeers = null)
            .AddIpfsIpns(ipnsKey, options =>
            {
                if (republishInterval is not null) options.RepublishInterval = republishInterval.Value;
            })
            .AddLogging()
            .BuildServiceProvider();
        await services.StartLibp2pAsync();

        IManagedPeerSwitch peerSwitch = services.GetRequiredService<IManagedPeerSwitch>();
        IListener listener = await peerSwitch.ListenAsync(localPeer);
        return new Node(services, peerSwitch, localPeer,
            services.GetRequiredService<KadClient>(),
            services.GetRequiredService<IKadPeerTable>(),
            services.GetRequiredService<IpnsService>(),
            listener);
    }

    // A manifest CID (the golden vector's value): a real dag-cbor CID.
    private const string ManifestCid = "bafyreigqcwstshwy4qff3zot3tjtdirbtt72n6x3r6rjcfon7odxdsmy2a";
    private const string ManifestCid2 = "bafyreigqcwstshwy4qff3zot3tjtdirbtt72n6x3r6rjcfon7odxdsmy2b";

    [Test]
    public async Task Publish_PublisherOffline_ColdPeerResolvesTheExactRecord()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Identity ipnsKey = new();
        string name = ipnsKey.PeerId.ToString();

        Node publisher = await BuildNodeAsync(ipnsKey);
        Node holder = await BuildNodeAsync(ipnsKey);
        publisher.Client.AddPeer(holder.AsKadPeer);
        holder.Client.AddPeer(publisher.AsKadPeer);

        // The publisher's node identity is not the IPNS key: the
        // announce is a delegate publishing for the owner, and the
        // holder stores it on the record's own signature.
        Assert.That(publisher.Peer.Identity.PeerId.ToString(), Is.Not.EqualTo(name));
        IpnsRecord published = await publisher.Ipns.PublishAsync(ManifestCid, cts.Token);
        Assert.That(published.Sequence, Is.EqualTo(1));

        // The announcement landed on the holder.
        Assert.That(holder.Table.GetValue(IpnsRecord.DhtKey(name))?.Value,
            Is.EqualTo(published.Encode()));

        // The publisher goes offline; the cold peer knows only the holder.
        await publisher.Listener.DisconnectAsync();
        publisher.Services.Dispose();

        Node cold = await BuildNodeAsync(ipnsKey);
        cold.Client.AddPeer(holder.AsKadPeer);
        (IpnsRecord Record, string Source)? found = await cold.Ipns.ResolveAsync(name, cts.Token);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Value.Record.Value, Is.EqualTo(ManifestCid));
        Assert.That(found.Value.Record.Sequence, Is.EqualTo(1));
        Assert.That(found.Value.Record.Verify(DateTimeOffset.UtcNow).Ok, Is.True);
        Assert.That(found.Value.Source, Is.EqualTo("dht"));
    }

    [Test]
    public async Task Resolve_UnknownName_ReturnsNull()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Node node = await BuildNodeAsync(new Identity());
        Node other = await BuildNodeAsync(new Identity());
        node.Client.AddPeer(other.AsKadPeer);
        other.Client.AddPeer(node.AsKadPeer);

        Assert.That(await node.Ipns.ResolveAsync(new Identity().PeerId.ToString(), cts.Token), Is.Null);
    }

    [Test]
    public async Task StaleReplay_DoesNotOverwriteTheCurrentRecord()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Identity ipnsKey = new();
        string name = ipnsKey.PeerId.ToString();

        Node publisher = await BuildNodeAsync(ipnsKey);
        Node holder = await BuildNodeAsync(ipnsKey);
        publisher.Client.AddPeer(holder.AsKadPeer);
        holder.Client.AddPeer(publisher.AsKadPeer);

        IpnsRecord first = await publisher.Ipns.PublishAsync(ManifestCid, cts.Token);
        IpnsRecord second = await publisher.Ipns.PublishAsync(ManifestCid2, cts.Token);
        Assert.That(second.Sequence, Is.EqualTo(2));
        Assert.That(holder.Table.GetValue(IpnsRecord.DhtKey(name))?.Value,
            Is.EqualTo(second.Encode()));

        // Replay the first record at the holder, raw: it verifies
        // on its own terms, but it is stale — the held record wins.
        var replay = new Message
        {
            type = MessageType.PutValue,
            key = IpnsRecord.DhtKey(name),
            record = new Record { key = IpnsRecord.DhtKey(name), value = first.Encode(), timeReceived = KadClient.NowStamp() },
        };
        await publisher.Client.QueryAsync(holder.AsKadPeer, replay, cts.Token);
        Assert.That(holder.Table.GetValue(IpnsRecord.DhtKey(name))?.Value,
            Is.EqualTo(second.Encode()));

        // And a cold peer still resolves sequence 2.
        Node cold = await BuildNodeAsync(ipnsKey);
        cold.Client.AddPeer(holder.AsKadPeer);
        (IpnsRecord Record, string Source)? found = await cold.Ipns.ResolveAsync(name, cts.Token);
        Assert.That(found!.Value.Record.Sequence, Is.EqualTo(2));
        Assert.That(found.Value.Record.Value, Is.EqualTo(ManifestCid2));
    }

    [Test]
    public async Task ForgedRecord_IsNeverStored()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Identity ipnsKey = new();
        string name = ipnsKey.PeerId.ToString();

        Node publisher = await BuildNodeAsync(ipnsKey);
        Node holder = await BuildNodeAsync(ipnsKey);
        publisher.Client.AddPeer(holder.AsKadPeer);
        holder.Client.AddPeer(publisher.AsKadPeer);

        // A record for this name signed by someone else's key.
        Identity attacker = new();
        var forged = new IpnsRecord
        {
            Name = name,
            Value = ManifestCid2,
            Sequence = 99,
            Validity = DateTimeOffset.UtcNow.AddHours(24),
            ValidityType = IpnsRecord.ValidityTypeEol,
            TtlSeconds = IpnsRecord.DefaultTtlSeconds,
            PublicKey = attacker.PublicKey.Data.ToByteArray(),
        };
        // Sign with the attacker's key: the signature is valid for
        // the attacker's key, but the key does not derive the name.
        forged.Signature = attacker.Sign(forged.SignatureInput());

        var put = new Message
        {
            type = MessageType.PutValue,
            key = IpnsRecord.DhtKey(name),
            record = new Record { key = IpnsRecord.DhtKey(name), value = forged.Encode(), timeReceived = KadClient.NowStamp() },
        };
        await publisher.Client.QueryAsync(holder.AsKadPeer, put, cts.Token);
        Assert.That(holder.Table.GetValue(IpnsRecord.DhtKey(name)), Is.Null);
    }

    [Test]
    public async Task RepublishLoop_ReAnnouncesTheHeldRecord()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Identity ipnsKey = new();
        string name = ipnsKey.PeerId.ToString();

        Node publisher = await BuildNodeAsync(ipnsKey, republishInterval: TimeSpan.FromSeconds(2));
        Node holder = await BuildNodeAsync(ipnsKey);
        publisher.Client.AddPeer(holder.AsKadPeer);
        holder.Client.AddPeer(publisher.AsKadPeer);

        await publisher.Ipns.PublishAsync(ManifestCid, cts.Token);
        string firstSeen = holder.Table.GetValue(IpnsRecord.DhtKey(name))!.TimeReceived;

        // The loop re-puts within ~2 s; the holder's stamp must move.
        string? seen = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            seen = holder.Table.GetValue(IpnsRecord.DhtKey(name))?.TimeReceived;
            if (seen is not null && seen != firstSeen)
            {
                break;
            }
        }

        Assert.That(seen, Is.Not.Null.And.Not.EqualTo(firstSeen));
    }
}
