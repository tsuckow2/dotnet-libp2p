// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Ipfs.Bitswap;
using Nethermind.Ipfs.Kad;
using Nethermind.Ipfs.Status;
using Nethermind.Ipfs.UnixFs;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Extensions;
using Nethermind.Libp2p.Stack;

// A consumer of the Nethermind.Ipfs libraries:
//
//   serve <file> [port]                 seed a file, announce it on
//                                       the DHT, and serve it (Bitswap)
//   get <peer-address> <root-id> <out>  download it from that peer
//   providers <multihash-hex>           find a block's providers on the
//                                       public DHT, from bootstrap only
//   findnode <peer-id>                  walk the DHT toward a peer
//
// Block ids are sha2-256 multihashes of the content; a file is a
// manifest block over 256 KiB chunk blocks (BitswapSession.PutFileAsync).

if (args.Length == 0 || args[0] is not ("serve" or "get" or "providers" or "findnode" or "fetch" or "dial"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  serve <file> [port]");
    Console.WriteLine("  get <peer-address> <root-id-hex> <output-file>");
    Console.WriteLine("  providers <multihash-hex>");
    Console.WriteLine("  findnode <peer-id>");
    Console.WriteLine("  fetch <cid> <output-file>");
    Console.WriteLine("  dial <peer-address>");
    return;
}

var store = new InMemoryBlockStore();
var localPeer = new LocalPeer(new Identity());
localPeer.GetOrAddAddress("/ip4/0.0.0.0/tcp/0");

var serviceCollection = new ServiceCollection()
    .AddSingleton<IBlockStore>(store)
    .AddKadDht()
    .AddLibp2pLocalPeer(localPeer)
    .AddLibp2p(builder => builder.AddManagedProtocol(BitSwap11Protocol.Ref).AddAppLayerProtocol<KadDhtProtocol>())
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
            .SetMinimumLevel(Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("IPFS_LOG"), out LogLevel parsed)
                ? parsed
                : LogLevel.Information)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }));

// Serving is the long-lived mode: it also hosts the live status
// page (connections, peers, DHT) on loopback — IPFS_STATUS_PORT=0
// turns it off, IPFS_STATUS_BIND widens the bind deliberately.
if (args[0] == "serve"
    && int.TryParse(Environment.GetEnvironmentVariable("IPFS_STATUS_PORT") ?? "8190", out int statusPort)
    && statusPort > 0)
{
    serviceCollection.AddIpfsStatus(options =>
    {
        options.Port = statusPort;
        options.BindAddress = Environment.GetEnvironmentVariable("IPFS_STATUS_BIND") ?? "127.0.0.1";
    });
}

ServiceProvider services = serviceCollection.BuildServiceProvider();

ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IPFS");
IManagedPeerSwitch peerSwitch = services.GetRequiredService<IManagedPeerSwitch>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Bring-up: components with startup work (the Kad client's
// bootstrap seed) begin here; lookups await the seed itself.
await services.StartLibp2pAsync(cts.Token);

if (args[0] == "dial")
{
    try
    {
        IRemotePeerConnection connection = await peerSwitch.DialAsync((Multiaddress)args[1], cts.Token);
        logger.LogInformation("Dialed {addr}: connected", args[1]);
        await connection.DisconnectAsync();
    }
    catch (Exception e)
    {
        Console.WriteLine(e.ToString());
    }
}
else if (args[0] == "fetch")
{
    var fetcher = new Fetcher(peerSwitch, services.GetRequiredService<KadClient>(), logger);
    Cid cid = Cid.Parse(args[1]);
    string output = args[2];

    try
    {
        byte[] content = await UnixFsFile.ReadAsync(cid, fetcher.GetBlockAsync, cts.Token);
        await File.WriteAllBytesAsync(output, content, cts.Token);
        logger.LogInformation("Fetched file {cid}: {length} bytes, every block hash-verified", cid, content.Length);
    }
    catch (InvalidDataException)
    {
        IReadOnlyList<(string Name, Cid Cid)> entries = await UnixFsFile.ListDirectoryAsync(cid, fetcher.GetBlockAsync, cts.Token);
        Directory.CreateDirectory(output);
        logger.LogInformation("{cid} is a directory with {count} entries; fetching each file", cid, entries.Count);
        foreach ((string name, Cid child) in entries)
        {
            byte[] content = await UnixFsFile.ReadAsync(child, fetcher.GetBlockAsync, cts.Token);
            string path = Path.Combine(output, name);
            await File.WriteAllBytesAsync(path, content, cts.Token);
            logger.LogInformation("  {name}: {length} bytes", name, content.Length);
        }
    }
}
else if (args[0] is "providers" or "findnode")
{
    KadClient kad = services.GetRequiredService<KadClient>();
    logger.LogInformation("Walking the DHT...");

    if (args[0] == "providers")
    {
        IReadOnlyList<KadPeer> providers = await kad.FindProvidersAsync(BlockIds.FromHex(args[1]), cts.Token);
        logger.LogInformation("Found {count} provider(s) after learning {known} peers", providers.Count, kad.KnownPeers.Count);
        foreach (KadPeer provider in providers)
        {
            Console.WriteLine($"provider {provider.PeerId}");
            foreach (Multiaddress addr in provider.Addresses)
            {
                Console.WriteLine($"  {addr}");
            }
        }
    }
    else
    {
        IReadOnlyList<KadPeer> closest = await kad.FindNodeAsync(new PeerId(args[1]).Bytes, cts.Token);
        logger.LogInformation("Closest peers after learning {known} peers", kad.KnownPeers.Count);
        foreach (KadPeer peer in closest)
        {
            Console.WriteLine($"peer {peer.PeerId} at {string.Join(", ", peer.Addresses)}");
        }
    }
}
else if (args[0] == "serve")
{
    string file = args[1];
    await using FileStream source = File.OpenRead(file);
    byte[] rootId = await BitswapSession.PutFileAsync(store, source);
    logger.LogInformation("Seeded {file}: {chunks} block(s)", file, (source.Length + BitswapSession.ChunkSize - 1) / BitswapSession.ChunkSize + 1);

    IListener listener = await peerSwitch.ListenAsync(localPeer, cts.Token);
    logger.LogInformation("Serving at {address}", listener.Address);
    logger.LogInformation("Root id: {rootId}", Convert.ToHexString(rootId));

    // Announce every block to the peers closest to it, and keep
    // announcing: provider records expire (a day on this
    // implementation, similar on others), so a long-running seed
    // re-provides or it fades out of the network's answers.
    KadClient kad = services.GetRequiredService<KadClient>();
    async Task ProvideAllAsync()
    {
        IReadOnlyCollection<byte[]> blockIds = store.StoredBlockIds;
        foreach (byte[] blockId in blockIds)
        {
            await kad.ProvideAsync(blockId, cts.Token);
        }

        logger.LogInformation("Provided {count} block record(s) to the DHT", blockIds.Count);
    }

    await ProvideAllAsync();
    _ = Task.Run(async () =>
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(12), cts.Token);
                await ProvideAllAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (TaskCanceledException)
    {
    }
    await listener.DisconnectAsync();
}
else
{
    Multiaddress remoteAddr = args[1];
    byte[] rootId = BlockIds.FromHex(args[2]);
    string outputFile = args[3];

    logger.LogInformation("Dialing {remote}", remoteAddr);
    IRemotePeerConnection connection = await peerSwitch.DialAsync(remoteAddr, cts.Token);

    IManagedProtocolComm<BitswapSession> bitswap = await connection.Managed.DialAsync(BitSwap11Protocol.Ref, cts.Token);
    bitswap.comm.OnBlock += (id, data) => logger.LogInformation("Received block {id} ({length} bytes)", Convert.ToHexString(id)[..12], data.Length);

    await using FileStream destination = File.Create(outputFile);
    await bitswap.comm.GetFileAsync(rootId, destination, cts.Token);
    logger.LogInformation("Saved {file} ({length} bytes)", outputFile, destination.Length);

    await connection.DisconnectAsync();
}

/// <summary>
///     Block fetching for the peerless modes: a working Bitswap
///     session is kept and reused; when it cannot serve a block, the
///     DHT is walked for that block's providers and each is tried in
///     turn. A block only ever comes back hash-verified — the
///     session completes a want solely on a content hash match.
/// </summary>
class Fetcher(IManagedPeerSwitch peerSwitch, KadClient kad, ILogger logger)
{
    private BitswapSession? _session;

    public async Task<byte[]> GetBlockAsync(Cid cid, CancellationToken token)
    {
        if (_session != null)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(40));
            try
            {
                return await _session.GetBlockAsync(cid, attempt.Token);
            }
            catch (Exception)
            {
                _session = null;
            }
        }

        IReadOnlyList<KadPeer> providers = await kad.FindProvidersAsync(cid.Multihash, token);
        if (providers.Count == 0)
        {
            // A walk can come back empty-handed in a ghost-heavy
            // region; the peer book it built makes a second walk
            // start closer. Real clients are persistent here too.
            logger.LogInformation("DHT: no providers on the first walk; walking again");
            providers = await kad.FindProvidersAsync(cid.Multihash, token);
        }

        logger.LogInformation("DHT: {count} provider(s) for {cid}", providers.Count, cid);

        foreach (KadPeer provider in providers.Take(40))
        {
            foreach (Multiaddress addr in provider.DialAddresses.Where(IsPubliclyDialable).Take(3))
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(25));
                string stage = "dial";
                try
                {
                    IRemotePeerConnection connection = await peerSwitch.DialAsync(addr, attempt.Token);
                    stage = "bitswap";
                    IManagedProtocolComm<BitswapSession> comm =
                        await connection.Managed.DialAsync(BitSwap11Protocol.Ref, attempt.Token);
                    stage = "block";
                    byte[] block = await comm.comm.GetBlockAsync(cid, attempt.Token);
                    _session = comm.comm;
                    logger.LogInformation("Served by {peer} at {addr}", provider.PeerId, addr);
                    return block;
                }
                catch (Exception e)
                {
                    logger.LogInformation("Provider {peer} at {addr}: {stage} failed: {error}",
                        provider.PeerId, addr, stage, e.Message);
                }
            }
        }

        throw new InvalidOperationException($"No provider could serve {cid}.");
    }

    // Prefer addresses a public client can plausibly reach: relay
    // circuits are dialable now (the circuit transport serves them),
    // but no websockets (not registered), and no private or loopback
    // IPs, which the DHT is full of. A circuit address is judged by
    // its relay's endpoint — the only part that must be public.
    private static bool IsPubliclyDialable(Multiaddress addr)
    {
        string text = addr.ToString();
        if (text.Contains("/ws") || text.Contains("/ip6/"))
        {
            return false;
        }

        return !text.Contains("/ip4/10.") && !text.Contains("/ip4/192.168.") &&
               !text.Contains("/ip4/127.") && !System.Text.RegularExpressions.Regex.IsMatch(text, @"/ip4/172\.(1[6-9]|2\d|3[01])\.");
    }
}
