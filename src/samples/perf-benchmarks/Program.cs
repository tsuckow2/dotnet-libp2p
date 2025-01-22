// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using DataTransferBenchmark;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using System.Net.Quic;
using Microsoft.Extensions.Logging;

await Task.Delay(1000);
{
    var localIdentity = new Identity(Enumerable.Repeat((byte)42, 32).ToArray());
    var localPeer = new Nethermind.Libp2p.Core.PeerFactory.LocalPeer(localIdentity);
    if (QuicListener.IsSupported)
    {
        localPeer.GetOrAddAddress("/ip4/0.0.0.0/udp/0/quic-v1");
    }
    localPeer.GetOrAddAddress("/ip4/0.0.0.0/tcp/0");


    ServiceProvider serviceProvider = new ServiceCollection()
.AddLibp2pLocalPeer(localPeer)
        .AddLibp2p(builder => builder.AddAppLayerProtocol<PerfProtocol>())
        .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information).AddSimpleConsole(l => l.SingleLine = true))
        .BuildServiceProvider();

    IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;


    IListener listener = await peer.ListenAsync($"/ip4/0.0.0.0/tcp/0/p2p/{peer.Identity.PeerId}");

    Multiaddress remoteAddr = listener.Address;
    ILocalPeer localPeer = peerFactory.Create();
    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr);

    Stopwatch timeSpent = Stopwatch.StartNew();
    await remotePeer.DialAsync<PerfProtocol>();
    TimeSpan elapsed = timeSpent.Elapsed;
    Console.WriteLine("Libp2p");
    Console.WriteLine("Elapsed {0}", timeSpent.Elapsed);
    Console.WriteLine("Speed {0:0.00} MiB/s", PerfProtocol.TotalLoad / timeSpent.Elapsed.TotalMilliseconds * 1000 / 1024 / 1024);
    await remotePeer.DisconnectAsync();
}
await Task.Delay(1000);

{
    IPeerFactory peerFactory = NoStackPeerFactoryBuilder.Create
        .AddAppLayerProtocol<PerfProtocol>()
        .Build();

    ILocalPeer peer = peerFactory.Create();
    IListener listener = await peer.ListenAsync($"/ip4/0.0.0.0/tcp/0");

    Multiaddress remoteAddr = listener.Address;
    ILocalPeer localPeer = peerFactory.Create();
    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr);

    Stopwatch timeSpent = Stopwatch.StartNew();
    await remotePeer.DialAsync<PerfProtocol>();
    TimeSpan elapsed = timeSpent.Elapsed;
    Console.WriteLine("NoStack");
    Console.WriteLine("Elapsed {0}", timeSpent.Elapsed);
    Console.WriteLine("Speed {0:0.00} MiB/s", PerfProtocol.TotalLoad / timeSpent.Elapsed.TotalMilliseconds * 1000 / 1024 / 1024);
    await remotePeer.DisconnectAsync();
}
