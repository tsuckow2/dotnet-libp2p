// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Protocols;

public class IpTcpProtocol(ILoggerFactory? loggerFactory = null) : ITransport, IProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<IpTcpProtocol>();

    public string Id => "ip-tcp";

    public string AddressProtocol => "tcp";

    public async Task ListenAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        if (channelFactory is null)
        {
            throw new Exception("Protocol is not properly instantiated");
        }

        Multiaddress addr = connection.LocalPeer.Address;
        bool isIP4 = addr.Has<IP4>();
        MultiaddressProtocol ipProtocol = isIP4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = int.Parse(addr.Get<TCP>().ToString());

        Socket srv = new(SocketType.Stream, ProtocolType.Tcp);
        srv.Bind(new IPEndPoint(ipAddress, tcpPort));
        srv.Listen(tcpPort);
        singalingChannel.GetAwaiter().OnCompleted(() =>
        {
            srv.Close();
        });

        IPEndPoint localIpEndpoint = (IPEndPoint)srv.LocalEndPoint!;


        Multiaddress localMultiaddress = new();
        localMultiaddress = isIP4 ? localMultiaddress.Add<IP4>(localIpEndpoint.Address.MapToIPv4()) : localMultiaddress.Add<IP6>(localIpEndpoint.Address.MapToIPv6());
        localMultiaddress = localMultiaddress.Add<TCP>(localIpEndpoint.Port);
        connection.LocalEndpoint = localMultiaddress;

        if (tcpPort == 0)
        {
            connection.LocalPeer.Address = connection.LocalPeer.Address
                .ReplaceOrAdd<TCP>(localIpEndpoint.Port);
        }

        _logger?.LogDebug("Ready to handle connections");
        connection.ListenerReady();

        await Task.Run(async () =>
        {
            for (; ; )
            {
                Socket client = await srv.AcceptAsync();
                IRemotePeerConnection clientConnection = channelFactory.AcceptConnection();
                IPEndPoint remoteIpEndpoint = (IPEndPoint)client.RemoteEndPoint!;

                Multiaddress remoteMultiaddress = new();
                remoteMultiaddress = isIP4 ? remoteMultiaddress.Add<IP4>(remoteIpEndpoint.Address.MapToIPv4()) : remoteMultiaddress.Add<IP6>(remoteIpEndpoint.Address.MapToIPv6());
                remoteMultiaddress = remoteMultiaddress.Add<TCP>(remoteIpEndpoint.Port);

                clientConnection.RemoteEndpoint = clientConnection.RemotePeer.Address = remoteMultiaddress;

                IChannel upChannel = channelFactory.SubListen(clientConnection);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (; ; )
                        {
                            if (client.Available == 0)
                            {
                                await Task.Yield();
                            }

                            byte[] buf = new byte[1024];
                            int length = await client.ReceiveAsync(buf, SocketFlags.None);
                            if (length != 0)
                            {
                                if ((await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..length]))) != IOResult.Ok)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    catch (SocketException e)
                    {
                        await upChannel.CloseAsync();
                    }
                });
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                        {
                            await client.SendAsync(data.ToArray(), SocketFlags.None);
                        }
                    }
                    catch (SocketException)
                    {
                        _logger?.LogInformation($"Disconnected({remoteMultiaddress}) due to a socket exception");
                        await upChannel.CloseAsync();
                    }
                });
            }
        });
    }

    public async Task DialAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        if (channelFactory is null)
        {
            throw new ProtocolViolationException();
        }

        Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        Multiaddress addr = connection.RemotePeer.Address;
        IPAddress ipAddress = await MultiaddressEndpoint.ResolveIpAsync(addr);
        int tcpPort = addr.Get<TCP>().Port;

        _logger?.LogDebug("Dialing {0}:{1}", ipAddress, tcpPort);

        try
        {
            await client.ConnectAsync(new IPEndPoint(ipAddress, tcpPort));
        }
        catch (SocketException e)
        {
            _logger?.LogDebug($"Failed to connect {addr}");
            _logger?.LogTrace($"Failed with {e.GetType()}: {e.Message}");
            _ = singalingChannel.CloseAsync();
            return;
        }

        singalingChannel.GetAwaiter().OnCompleted(() =>
        {
            client.Close();
        });

        IPEndPoint localEndpoint = (IPEndPoint)client.LocalEndPoint!;
        IPEndPoint remoteEndpoint = (IPEndPoint)client.RemoteEndPoint!;

        var isIP4 = addr.Has<IP4>();

        var remoteMultiaddress = new Multiaddress();
        var remoteIpAddress = isIP4 ? remoteEndpoint.Address.MapToIPv4() : remoteEndpoint.Address.MapToIPv6();
        remoteMultiaddress = isIP4 ? remoteMultiaddress.Add<IP4>(remoteIpAddress) : remoteMultiaddress.Add<IP6>(remoteIpAddress);
        connection.RemoteEndpoint = remoteMultiaddress.Add<TCP>(remoteEndpoint.Port);


        var localMultiaddress = new Multiaddress();
        var localIpAddress = isIP4 ? localEndpoint.Address.MapToIPv4() : localEndpoint.Address.MapToIPv6();
        localMultiaddress = isIP4 ? localMultiaddress.Add<IP4>(localIpAddress) : localMultiaddress.Add<IP6>(localIpAddress);
        connection.LocalEndpoint = localMultiaddress.Add<TCP>(localEndpoint.Port);

        connection.LocalPeer.Address = connection.LocalEndpoint!.Add<P2P>(connection.LocalPeer.Identity.PeerId.ToString());

        IChannel upChannel = channelFactory.SubDial(connection);

        Task receiveTask = Task.Run(async () =>
        {
            byte[] buf = new byte[client.ReceiveBufferSize];
            try
            {
                for (; ; )
                {
                    int dataLength = await client.ReceiveAsync(buf, SocketFlags.None);
                    if (dataLength != 0)
                    {
                        _logger?.LogDebug("Receive {0} data, len={1}", addr, dataLength);
                        if ((await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf[..dataLength]))) != IOResult.Ok)
                        {
                            break;
                        };
                    }
                }

            }
            catch (SocketException)
            {
                _ = upChannel.CloseAsync();
            }
        });

        Task sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    _logger?.LogDebug("Send {0} data, len={1}", addr, data.Length);
                    await client.SendAsync(data.ToArray(), SocketFlags.None);
                }
            }
            catch (SocketException)
            {
                _ = upChannel.CloseAsync();
            }
        });

        await Task.WhenAll(receiveTask, sendTask);
        _ = upChannel.CloseAsync();
    }

    public async Task<IRemoteComm> DialAsync(Multiaddress address)
    {
        Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        IPAddress ipAddress = await MultiaddressEndpoint.ResolveIpAsync(address);
        int tcpPort = address.Get<TCP>().Port;

        _logger?.LogDebug("Dialing {0}:{1}", ipAddress, tcpPort);

        try
        {
            await client.ConnectAsync(new IPEndPoint(ipAddress, tcpPort));
        }
        catch (SocketException e)
        {
            _logger?.LogDebug($"Failed to connect {address}");
            _logger?.LogTrace($"Failed with {e.GetType()}: {e.Message}");
            throw;
        }

        var stream = new NetworkStream(client, true /* Closing Stream, closes socket */);
        var connection = new GenericRemoteConnection(address, () => { stream.Close(); return Task.CompletedTask; });
        return new GenericRemoteComm(false, new StreamChannel(stream), connection);
    }

    public async Task ListenAsync(Multiaddress localAddress, Action<IChannel, Multiaddress> onConnection, CancellationToken token)
    {
        var isIP4 = localAddress.Has<IP4>();
        MultiaddressProtocol ipProtocol = isIP4 ? localAddress.Get<IP4>() : localAddress.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = localAddress.Get<TCP>().Port;

        TcpListener listener = new(new IPEndPoint(ipAddress, tcpPort));
        listener.Start();
        token.Register(() => listener.Stop());
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(token);
                IPEndPoint remoteEndpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
                var remoteAddress = isIP4
                    ? new Multiaddress().Add<IP4>(remoteEndpoint.Address.MapToIPv4()).Add<TCP>(remoteEndpoint.Port)
                    : new Multiaddress().Add<IP6>(remoteEndpoint.Address.MapToIPv6()).Add<TCP>(remoteEndpoint.Port);
                var stream = client.GetStream();
                onConnection(new StreamChannel(stream), remoteAddress);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
        }
    }
}