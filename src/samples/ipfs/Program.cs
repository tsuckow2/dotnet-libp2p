// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Ipfs.Bitswap;
using Nethermind.Ipfs.Kad;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Stack;

// A consumer of the Nethermind.Ipfs libraries:
//
//   serve <file> [port]                 seed a file and serve it (Bitswap)
//   get <peer-address> <root-id> <out>  download it from that peer
//   providers <multihash-hex>           find a block's providers on the
//                                       public DHT, from bootstrap only
//   findnode <peer-id>                  walk the DHT toward a peer
//
// Block ids are sha2-256 multihashes of the content; a file is a
// manifest block over 256 KiB chunk blocks (BitswapSession.PutFileAsync).

if (args.Length == 0 || args[0] is not ("serve" or "get" or "providers" or "findnode" or "fetch"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  serve <file> [port]");
    Console.WriteLine("  get <peer-address> <root-id-hex> <output-file>");
    Console.WriteLine("  providers <multihash-hex>");
    Console.WriteLine("  findnode <peer-id>");
    Console.WriteLine("  fetch <cid> <output-file>");
    return;
}

var store = new InMemoryBlockStore();
var localPeer = new LocalPeer(new Identity());
localPeer.GetOrAddAddress("/ip4/0.0.0.0/tcp/0");

ServiceProvider services = new ServiceCollection()
    .AddSingleton<IBlockStore>(store)
    .AddSingleton<KadClient>()
    .AddLibp2pLocalPeer(localPeer)
    .AddLibp2p(builder => builder.AddManagedProtocol(BitSwap11Protocol.Ref).AddAppLayerProtocol<KadDhtProtocol>())
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IPFS");
IManagedPeerSwitch peerSwitch = services.GetRequiredService<IManagedPeerSwitch>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (args[0] == "fetch")
{
    var fetcher = new Fetcher(peerSwitch, services.GetRequiredService<KadClient>(), logger);
    Cid cid = Cid.Parse(args[1]);
    byte[] block = await fetcher.GetBlockAsync(cid, cts.Token);
    logger.LogInformation("Fetched {cid}: {length} bytes, hash-verified", cid, block.Length);
    await File.WriteAllBytesAsync(args[2], block, cts.Token);
    logger.LogInformation("Wrote {file}", args[2]);
}
else if (args[0] is "providers" or "findnode")
{
    KadClient kad = services.GetRequiredService<KadClient>();
    kad.SeedBootstrapPeers();
    logger.LogInformation("Seeded {count} bootstrap peers; walking the DHT...", KadDht.BootstrapPeers.Count);

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

        kad.SeedBootstrapPeers();
        IReadOnlyList<KadPeer> providers = await kad.FindProvidersAsync(cid.Multihash, token);
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

    // Prefer addresses a public client can plausibly reach: no relay
    // circuits (unsupported), no websockets (not registered), and no
    // private or loopback IPs, which the DHT is full of.
    private static bool IsPubliclyDialable(Multiaddress addr)
    {
        string text = addr.ToString();
        if (text.Contains("/p2p-circuit") || text.Contains("/ws") || text.Contains("/ip6/"))
        {
            return false;
        }

        return !text.Contains("/ip4/10.") && !text.Contains("/ip4/192.168.") &&
               !text.Contains("/ip4/127.") && !System.Text.RegularExpressions.Regex.IsMatch(text, @"/ip4/172\.(1[6-9]|2\d|3[01])\.");
    }
    }
}
