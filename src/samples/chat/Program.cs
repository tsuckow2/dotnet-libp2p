// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using System.Net.Quic;

var localIdentity = new Identity();
var localPeer = new LocalPeer(localIdentity);
if (QuicListener.IsSupported)
{
    localPeer.GetOrAddAddress("/ip4/0.0.0.0/udp/0/quic-v1");
}
localPeer.GetOrAddAddress("/ip4/0.0.0.0/tcp/0");

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2pLocalPeer(localPeer)
    .AddLibp2p(builder => builder.AddManagedProtocol(ChatProtocol.Ref))
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("Chat");
IManagedPeerSwitch peerSwitch = serviceProvider.GetService<IManagedPeerSwitch>()!;

CancellationTokenSource ts = new();

if (args.Length > 0 && args[0] == "-d")
{
    Multiaddress remoteAddr = args[1];

    logger.LogInformation("Dialing {remote}", remoteAddr);
    IRemotePeerConnection connection = await peerSwitch.DialAsync(remoteAddr, ts.Token);

    IManagedProtocolComm<ChatProtocolResult> chat = await connection.Managed.DialAsync(ChatProtocol.Ref, ts.Token);
    await chat.comm.Exit;
    await connection.DisconnectAsync();
}
else
{
    string addrTemplate = args.Contains("-quic") ?
        "/ip4/0.0.0.0/udp/{0}/quic-v1" :
        "/ip4/0.0.0.0/tcp/{0}";

    localPeer.Address = string.Format(addrTemplate, args.Length > 0 && args[0] == "-sp" ? args[1] : "0");

    IListener listener = await peerSwitch.ListenAsync(localPeer, ts.Token);
    logger.LogInformation("Listener started at {address}", listener.Address);
    listener.OnConnection += async remotePeer => logger.LogInformation("A peer connected {remote}", remotePeer.Address);
    Console.CancelKeyPress += delegate { listener.DisconnectAsync(); };

    await listener;
}
