// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nethermind.Libp2p.Protocols.Tests;

/// <summary>
/// Standalone-path tests for QuicheTransport: dial/listen/echo
/// through the ITransport API only (no switch, no stack). Needs UDP,
/// so these run on Forge and in CI, not on the sandbox VM.
/// </summary>
[TestFixture]
[NonParallelizable]
public class QuicheTransportTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static (int First, int Second) TwoFreeUdpPorts()
    {
        // Held open together so the OS cannot hand out the same port twice.
        using var first = new Socket(SocketType.Dgram, ProtocolType.Udp);
        using var second = new Socket(SocketType.Dgram, ProtocolType.Udp);
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        second.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (((IPEndPoint)first.LocalEndPoint!).Port, ((IPEndPoint)second.LocalEndPoint!).Port);
    }

    private static Task EchoLoop(IChannel channel, CancellationToken token) => Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                ReadResult result = await channel.ReadAsync(1024, ReadBlockingMode.WaitAny, token);
                if (result.Result != IOResult.Ok)
                {
                    break;
                }
                await channel.WriteAsync(result.Data, token);
            }
        }
        catch (Exception)
        {
            // The connection closed; the stream is done.
        }
    });

    private static Multiaddress LoopbackQuic(int port) => new Multiaddress()
        .Add<IP4>(IPAddress.Loopback)
        .Add<UDP>(port)
        .Add<QUICv1>();

    private static async Task<byte[]> ReadExactAsync(IChannel channel, int length, CancellationToken token)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            ReadResult result = await channel.ReadAsync(length - offset, ReadBlockingMode.WaitAll, token);
            if (result.Result != IOResult.Ok)
            {
                break;
            }
            ReadOnlySequence<byte> data = result.Data;
            data.CopyTo(buffer.AsSpan(offset));
            offset += (int)data.Length;
        }
        if (offset != length)
        {
            throw new IOException($"Stream ended after {offset} of {length} bytes.");
        }
        return buffer;
    }

    [Test]
    [Timeout(30000)]
    public async Task DialListenEcho()
    {
        int port = FreeUdpPort();
        using var listenerTransport = new QuicheTransport();
        using var dialerTransport = new QuicheTransport();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Echo loop: every inbound stream echoes its bytes back.
        Task listenTask = listenerTransport.ListenAsync(LoopbackQuic(port), (channel, remoteAddress) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        ReadResult result = await channel.ReadAsync(1024, ReadBlockingMode.WaitAny, testCts.Token);
                        if (result.Result != IOResult.Ok)
                        {
                            break;
                        }
                        await channel.WriteAsync(result.Data, testCts.Token);
                    }
                }
                catch (Exception)
                {
                    // The connection closed; the stream is done.
                }
            });
        }, testCts.Token);

        // ListenAsync binds synchronously before its first await, so
        // the socket is listening by the time this returns.
        IRemoteComm comm = await dialerTransport.DialAsync(LoopbackQuic(port));
        byte[] hello = Encoding.UTF8.GetBytes("hello");
        await comm.Channel.WriteAsync(new ReadOnlySequence<byte>(hello), testCts.Token);
        byte[] echo = await ReadExactAsync(comm.Channel, hello.Length, testCts.Token);
        Assert.That(echo, Is.EqualTo(hello));

        await comm.Connection.DisconnectAsync();
        testCts.Cancel();
        await listenTask;
    }

    /// <summary>
    /// Phase 2: the shared-port dial. Each transport listens on its
    /// own port and then dials the other through the transport API;
    /// the listener's view of the inbound connection's remote port
    /// must be the dialer's listen port — the dial went out on the
    /// listener's socket, not an ephemeral one. Echo both ways.
    /// </summary>
    [Test]
    [Timeout(30000)]
    public async Task DialFromListenPort()
    {
        (int portA, int portB) = TwoFreeUdpPorts();
        using var transportA = new QuicheTransport();
        using var transportB = new QuicheTransport();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var seenByB = new TaskCompletionSource<Multiaddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenByA = new TaskCompletionSource<Multiaddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task listenTaskB = transportB.ListenAsync(LoopbackQuic(portB), (channel, remote) =>
        {
            seenByB.TrySetResult(remote);
            _ = EchoLoop(channel, testCts.Token);
        }, testCts.Token);
        Task listenTaskA = transportA.ListenAsync(LoopbackQuic(portA), (channel, remote) =>
        {
            seenByA.TrySetResult(remote);
            _ = EchoLoop(channel, testCts.Token);
        }, testCts.Token);

        // A dials B: B must see source port A.
        IRemoteComm commAB = await transportA.DialAsync(LoopbackQuic(portB));
        Multiaddress remoteAtB = await seenByB.Task.WaitAsync(testCts.Token);
        Assert.That(remoteAtB.Get<UDP>().Port, Is.EqualTo(portA));
        byte[] hello = Encoding.UTF8.GetBytes("hello");
        await commAB.Channel.WriteAsync(new ReadOnlySequence<byte>(hello), testCts.Token);
        Assert.That(await ReadExactAsync(commAB.Channel, hello.Length, testCts.Token), Is.EqualTo(hello));

        // B dials A: A must see source port B.
        IRemoteComm commBA = await transportB.DialAsync(LoopbackQuic(portA));
        Multiaddress remoteAtA = await seenByA.Task.WaitAsync(testCts.Token);
        Assert.That(remoteAtA.Get<UDP>().Port, Is.EqualTo(portB));
        await commBA.Channel.WriteAsync(new ReadOnlySequence<byte>(hello), testCts.Token);
        Assert.That(await ReadExactAsync(commBA.Channel, hello.Length, testCts.Token), Is.EqualTo(hello));

        await commAB.Connection.DisconnectAsync();
        await commBA.Connection.DisconnectAsync();
        testCts.Cancel();
        await listenTaskB;
        await listenTaskA;
    }

    [Test]
    [Timeout(30000)]
    public async Task DialDeadPortThrows()
    {
        using var transport = new QuicheTransport();
        OperationCanceledException? caught = null;
        try
        {
            await transport.DialAsync(LoopbackQuic(FreeUdpPort()));
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }
        // The only cancellation source is the transport's dial
        // timeout: a dead port never answers the handshake.
        Assert.That(caught, Is.Not.Null);
    }
}
