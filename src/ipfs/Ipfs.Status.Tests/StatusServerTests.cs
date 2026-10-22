// SPDX-FileCopyrightText: 2026 Thomas Suckow
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Ipfs.Kad;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Extensions;
using Nethermind.Libp2p.Stack;
using NUnit.Framework;

namespace Nethermind.Ipfs.Status.Tests;

/// <summary>
///     The status server over real loopback nodes with the
///     production wiring: the page and its JSON are served, the
///     snapshot names the local peer and its advertised address,
///     a dialed peer appears as a live connection with its dial
///     evidence (and leaves when it disconnects), and a stored
///     provider record shows in the DHT section.
/// </summary>
[TestFixture]
[NonParallelizable]
public class StatusServerTests
{
    private sealed record Node(
        ServiceProvider Services,
        IManagedPeerSwitch Switch,
        LocalPeer Peer,
        IListener Listener,
        HttpClient Http,
        StatusServer Server);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<Node> BuildNodeAsync(bool withStatus)
    {
        var localPeer = new LocalPeer(new Identity());
        localPeer.GetOrAddAddress("/ip4/127.0.0.1/tcp/0");
        int statusPort = FreePort();

        IServiceCollection collection = new ServiceCollection()
            .AddLibp2pLocalPeer(localPeer)
            .AddLibp2p(builder => builder.AddAppLayerProtocol<KadDhtProtocol>())
            .AddKadDht(options => options.BootstrapPeers = null)
            .AddLogging();
        if (withStatus)
        {
            collection.AddIpfsStatus(options => options.Port = statusPort);
        }

        ServiceProvider services = collection.BuildServiceProvider();
        await services.StartLibp2pAsync();

        IManagedPeerSwitch peerSwitch = services.GetRequiredService<IManagedPeerSwitch>();
        IListener listener = await peerSwitch.ListenAsync(localPeer);
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{statusPort}/") };
        return new Node(services, peerSwitch, localPeer, listener, http,
            services.GetService<StatusServer>()!);
    }

    private static async Task<JsonElement> GetSnapshotAsync(HttpClient http)
    {
        using HttpResponseMessage response = await http.GetAsync("status.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> PollSnapshotAsync(
        HttpClient http, Func<JsonElement, bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            JsonElement snapshot = await GetSnapshotAsync(http);
            if (condition(snapshot) || DateTimeOffset.UtcNow > deadline)
            {
                return snapshot;
            }

            await Task.Delay(200);
        }
    }

    [Test]
    public async Task ServesThePage_AndASnapshotOfTheLocalPeer()
    {
        Node node = await BuildNodeAsync(withStatus: true);

        using HttpResponseMessage page = await node.Http.GetAsync("");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await page.Content.ReadAsStringAsync(), Does.Contain("libp2p node status"));

        JsonElement snapshot = await GetSnapshotAsync(node.Http);
        JsonElement local = snapshot.GetProperty("local");
        Assert.That(local.GetProperty("peerId").GetString(),
            Is.EqualTo(node.Peer.Identity.PeerId.ToString()));

        // The advertised set is the book's concrete form of the
        // listen address — the switch's stamped listener address,
        // peer id included (Identify strips it at send time; the
        // book, and so the snapshot, carries it).
        Assert.That(
            local.GetProperty("advertisedAddresses").EnumerateArray().Select(a => a.GetString()),
            Does.Contain(node.Listener.Address.ToString()));
        Assert.That(snapshot.GetProperty("dht").GetProperty("knownPeers").GetArrayLength(),
            Is.EqualTo(0));

        await node.Server.StopAsync();
        await node.Listener.DisconnectAsync();
    }

    [Test]
    public async Task DialedPeer_AppearsAsALiveConnection_ThenLeaves()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Node dialer = await BuildNodeAsync(withStatus: true);
        Node listener = await BuildNodeAsync(withStatus: false);

        IRemotePeer peer = dialer.Switch.GetRemotePeer(listener.Peer.Identity);
        peer.GetOrAddAddress(listener.Listener.Address);
        IRemotePeerConnection connection = await dialer.Switch.DialAsync(peer, cts.Token);

        JsonElement snapshot = await PollSnapshotAsync(dialer.Http,
            s => s.GetProperty("connections").GetArrayLength() == 1, TimeSpan.FromSeconds(15));
        JsonElement live = snapshot.GetProperty("connections")[0];
        Assert.That(live.GetProperty("remotePeerId").GetString(),
            Is.EqualTo(listener.Peer.Identity.PeerId.ToString()));
        Assert.That(live.GetProperty("provenPeerId").GetString(),
            Is.EqualTo(listener.Peer.Identity.PeerId.ToString()));
        Assert.That(live.GetProperty("viaRelay").GetBoolean(), Is.False);

        JsonElement peerStatus = snapshot.GetProperty("peers").EnumerateArray()
            .Single(p => p.GetProperty("peerId").GetString()
                == listener.Peer.Identity.PeerId.ToString());
        Assert.That(peerStatus.GetProperty("connected").GetBoolean(), Is.True);
        Assert.That(peerStatus.GetProperty("addresses").EnumerateArray()
            .Any(a => a.GetProperty("successfulDials").GetInt32() >= 1), Is.True);

        await connection.DisconnectAsync();
        JsonElement after = await PollSnapshotAsync(dialer.Http,
            s => s.GetProperty("connections").GetArrayLength() == 0, TimeSpan.FromSeconds(15));
        Assert.That(after.GetProperty("connections").GetArrayLength(), Is.EqualTo(0));

        await dialer.Server.StopAsync();
        await dialer.Listener.DisconnectAsync();
        await listener.Listener.DisconnectAsync();
    }

    [Test]
    public async Task StoredProviderRecord_ShowsInTheDhtSection()
    {
        Node node = await BuildNodeAsync(withStatus: true);
        byte[] key = SHA256.HashData("a block this node provides"u8.ToArray());
        var provider = new KadPeer(node.Peer.Identity.PeerId.Bytes, [node.Listener.Address]);
        node.Services.GetRequiredService<ProviderStore>().Add(key, provider);

        JsonElement snapshot = await GetSnapshotAsync(node.Http);
        JsonElement records = snapshot.GetProperty("dht").GetProperty("providerRecords");
        Assert.That(records.GetArrayLength(), Is.EqualTo(1));
        Assert.That(records[0].GetProperty("key").GetString(),
            Is.EqualTo(Convert.ToHexString(key)));
        Assert.That(records[0].GetProperty("providerPeerId").GetString(),
            Is.EqualTo(node.Peer.Identity.PeerId.ToString()));

        await node.Server.StopAsync();
        await node.Listener.DisconnectAsync();
    }
}
