// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using System.Net.Quic;

// Bitswap sample host: the same shape as the chat sample, with the
// Bitswap 1.1 protocol registered as a managed-tier app protocol.
// Received wants/blocks are logged; a block-exchange engine would hook
// BitSwapProtocolChannel's events to a block store.

var localIdentity = new Identity();
var localPeer = new LocalPeer(localIdentity);
if (QuicListener.IsSupported)
{
    localPeer.GetOrAddAddress("/ip4/0.0.0.0/udp/0/quic-v1");
}
localPeer.GetOrAddAddress("/ip4/0.0.0.0/tcp/0");

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2pLocalPeer(localPeer)
    .AddLibp2p(builder => builder.AddManagedProtocol(BitSwap11Protocol.Ref))
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("IPFS");
IManagedPeerSwitch peerSwitch = serviceProvider.GetService<IManagedPeerSwitch>()!;

CancellationTokenSource ts = new();

if (args.Length > 0 && args[0] == "-d")
{
    Multiaddress remoteAddr = args[1];

    logger.LogInformation("Dialing {remote}", remoteAddr);
    IRemotePeerConnection connection = await peerSwitch.DialAsync(remoteAddr, ts.Token);

    IManagedProtocolComm<BitSwapProtocolChannel> bitswap = await connection.Managed.DialAsync(BitSwap11Protocol.Ref, ts.Token);
    bitswap.comm.OnWant += (block, cancel) => logger.LogInformation("Remote wants {block} (cancel: {cancel})", Convert.ToHexString(block), cancel);
    bitswap.comm.OnBlock += (prefix, data) => logger.LogInformation("Received block, {length} bytes", data.Length);
    await Task.Delay(Timeout.Infinite, ts.Token);
}
else
{
    IListener listener = await peerSwitch.ListenAsync(localPeer, ts.Token);
    logger.LogInformation("Listener started at {address}", listener.Address);
    listener.OnConnection += async remotePeer => logger.LogInformation("A peer connected {remote}", remotePeer.Address);
    Console.CancelKeyPress += delegate { listener.DisconnectAsync(); };

    await listener;
}
