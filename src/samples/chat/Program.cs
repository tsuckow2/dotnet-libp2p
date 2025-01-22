// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using System.Net.Quic;


var localIdentity = new Identity();
var localPeer = new Nethermind.Libp2p.Core.PeerFactory.LocalPeer(localIdentity);
if (QuicListener.IsSupported)
{
    localPeer.GetOrAddAddress("/ip4/0.0.0.0/udp/0/quic-v1");
}
localPeer.GetOrAddAddress("/ip4/0.0.0.0/tcp/0");

ServiceProvider serviceProvider = new ServiceCollection()
.AddLibp2pLocalPeer(localPeer)
    .AddLibp2p(builder => builder.AddAppLayerProtocol<ChatProtocol>())
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("Chat");
IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

CancellationTokenSource ts = new();


var f = new Foo();
var result = await f.DialAsync(ChatProtocol.Ref);


if (args.Length > 0 && args[0] == "-d")
{
    Multiaddress remoteAddr = args[1];



    logger.LogInformation("Dialing {remote}", remoteAddr);
    IRemotePeerConnection remotePeer = await peerFactory.DialAsync(remoteAddr, ts.Token);

    var chat = ChatProtocol.Assert(await remotePeer.DialAsync<ChatProtocol>(ts.Token));
    await chat.Exit;
    await remotePeer.DisconnectAsync();
}
else
{
    Identity optionalFixedIdentity = new(Enumerable.Repeat((byte)42, 32).ToArray());
    ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    string addrTemplate = args.Contains("-quic") ?
        "/ip4/0.0.0.0/udp/{0}/quic-v1" :
        "/ip4/0.0.0.0/tcp/{0}";

    IListener listener = await peer.ListenAsync(
        string.Format(addrTemplate, args.Length > 0 && args[0] == "-sp" ? args[1] : "0"),
        ts.Token);
    logger.LogInformation("Listener started at {address}", listener.Address);
    listener.OnConnection += async remotePeer => logger.LogInformation("A peer connected {remote}", remotePeer.Address);
    Console.CancelKeyPress += delegate { listener.DisconnectAsync(); };

    await listener;
}
