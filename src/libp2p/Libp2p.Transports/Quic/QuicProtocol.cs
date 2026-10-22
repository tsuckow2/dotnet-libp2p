// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Quic;
using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols;

#pragma warning disable CA1416 // Do not inform about platform compatibility

/// <summary>
/// https://github.com/libp2p/specs/blob/master/quic/README.md
/// </summary>
[RequiresPreviewFeatures]
public class QuicProtocol : IProtocol, ITransport
{
    private readonly ILogger<QuicProtocol>? _logger;
    private readonly ECDsa _sessionKey;

    public QuicProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<QuicProtocol>();
        _sessionKey = ECDsa.Create();
    }

    private static readonly List<SslApplicationProtocol> protocols = new()
    {
        new SslApplicationProtocol("libp2p"),
        // SslApplicationProtocol.Http3, // webtransport
    };

    public string Id => "quic-v1";

    public string AddressProtocol => "quic-v1";

    public async Task ListenAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection peerConnection)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException($"The protocol requires {nameof(channelFactory)}");
        }

        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        Multiaddress addr = peerConnection.LocalPeer.Address;
        MultiaddressProtocol ipProtocol = addr.Has<IP4>() ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int udpPort = int.Parse(addr.Get<UDP>().ToString());

        IPEndPoint localEndpoint = new(ipAddress, udpPort);

        QuicServerConnectionOptions serverConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.

            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = protocols,
                // Mutual TLS is the protocol: the server must ask
                // for the client's certificate — without this, no
                // certificate is ever presented, the validation
                // callback never runs, and the connection is
                // encrypted but unauthenticated. Inbound has no
                // named expectation: the certificate proves whoever
                // the client is, and the accept loop publishes it.
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, c, _, _) =>
                    CertificateHelper.ValidateCertificate(c as X509Certificate2, null),
                ServerCertificate = CertificateHelper.CertificateFromIdentity(_sessionKey, peerConnection.LocalPeer.Identity)
            },
        };

        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = localEndpoint,
            ApplicationProtocols = protocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions)
        });

        var localEndPoint = new Multiaddress();
        // IP (4 or 6 is based on source address).
        var strLocalEndpoint = listener.LocalEndPoint.Address.ToString();
        localEndPoint = addr.Has<IP4>() ? localEndPoint.Add<IP4>(strLocalEndpoint) : localEndPoint.Add<IP6>(strLocalEndpoint);

        // UDP
        localEndPoint = localEndPoint.Add<UDP>(listener.LocalEndPoint.Port);

        // Set on the connection
        peerConnection.LocalEndpoint = localEndPoint;

        if (udpPort == 0)
        {
            peerConnection.LocalPeer.Address = peerConnection.LocalPeer.Address
                .ReplaceOrAdd<UDP>(listener.LocalEndPoint.Port);
        }


        _logger?.ReadyToHandleConnections();
        peerConnection.ListenerReady();
        TaskAwaiter signalingWawaiter = singalingChannel.GetAwaiter();

        signalingWawaiter.OnCompleted(() =>
        {
            listener.DisposeAsync();
        });

        while (!signalingWawaiter.IsCompleted)
        {
            try
            {
                QuicConnection connection = await listener.AcceptConnectionAsync();
                IRemotePeerConnection accepted = channelFactory.AcceptConnection();

                // The handshake proved the client's identity: its
                // certificate binds its key (validated during the
                // handshake; the accepted connection carries that
                // very certificate). Establish the peer from the
                // proof, as the dial side does, instead of leaving
                // the naming of inbound peers to Identify.
                if (connection.RemoteCertificate is X509Certificate2 clientCertificate &&
                    CertificateHelper.ValidateCertificate(clientCertificate, null, out Identity? provenIdentity) &&
                    provenIdentity is not null)
                {
                    accepted.ProvenPeerId = provenIdentity.PeerId;
                    accepted.RemotePeer.Identity = provenIdentity;
                }

                _ = ProcessStreams(connection, accepted, channelFactory);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Closed with exception {exception}", ex.Message);
                _logger?.LogTrace("{stackTrace}", ex.StackTrace);
            }
        }
    }

    public async Task DialAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection peerConnection)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException($"The protocol requires {nameof(channelFactory)}");
        }

        if (!QuicConnection.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        // The local endpoint is an ephemeral wildcard of the
        // remote's family. (It used to be parsed out of the local
        // peer's address, which is the peer's LISTEN address — a
        // TCP one in any node that never configured QUIC listening —
        // and died on its missing UDP part.)
        Multiaddress addr = peerConnection.RemotePeer.Address;
        IPAddress ipAddress = await MultiaddressEndpoint.ResolveIpAsync(addr);
        int udpPort = int.Parse(addr.Get<UDP>().ToString()!);

        IPEndPoint localEndpoint = new(
            ipAddress.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        IPEndPoint remoteEndpoint = new(ipAddress, udpPort);

        Identity? provenIdentity = null;
        QuicClientConnectionOptions clientConnectionOptions = new()
        {
            LocalEndPoint = localEndpoint,
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.
            MaxInboundUnidirectionalStreams = 256,
            MaxInboundBidirectionalStreams = 256,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = null,
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, c, _, _) =>
                    CertificateHelper.ValidateCertificate(c as X509Certificate2, peerConnection.ExpectedPeerId?.ToString(), out provenIdentity),
                ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, peerConnection.LocalPeer.Identity) },
            },
            RemoteEndPoint = remoteEndpoint,
        };

        QuicConnection connection = await QuicConnection.ConnectAsync(clientConnectionOptions);

        // The certificate validation proved the remote's identity key
        // (and that it is the peer the dial named, when it named one):
        // establish the peer from the proof, as the security layer
        // does for Noise.
        if (provenIdentity is not null)
        {
            peerConnection.ProvenPeerId = provenIdentity.PeerId;
            peerConnection.RemotePeer.Identity = provenIdentity;
        }

        _logger?.Connected(connection.LocalEndPoint, connection.RemoteEndPoint);

        singalingChannel.GetAwaiter().OnCompleted(() =>
        {
            connection.CloseAsync(0);
        });

        await ProcessStreams(connection, peerConnection, channelFactory);
    }

    // ---- ITransport: the raw comm shape, one stream per comm ------------
    //
    // The stack path above binds the QUIC certificate to the peers'
    // identities. The standalone transport has no stack connection, so it
    // presents an ephemeral identity and accepts any libp2p certificate:
    // a raw pipe, with identity binding left to the layers above it —
    // the same contract IpTcpProtocol's ITransport methods offer.

    public async Task<IRemoteComm> DialAsync(Multiaddress address)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        var remoteEndPoint = new IPEndPoint(await MultiaddressEndpoint.ResolveIpAsync(address), address.Get<UDP>().Port);

        QuicConnection connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 1,
            MaxInboundUnidirectionalStreams = 256,
            MaxInboundBidirectionalStreams = 256,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = null,
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, new Identity()) },
            },
            RemoteEndPoint = remoteEndPoint,
        });

        QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        var transportConnection = new GenericRemoteConnection(address, async () =>
        {
            await connection.CloseAsync(0);
            await connection.DisposeAsync();
        });
        return new GenericRemoteComm(false, new StreamChannel(stream), transportConnection);
    }

    public async Task ListenAsync(Multiaddress localAddress, Action<IChannel, Multiaddress> onConnection, CancellationToken token)
    {
        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        MultiaddressProtocol protocol = localAddress.Has<IP4>() ? localAddress.Get<IP4>() : localAddress.Get<IP6>();
        var listenEndPoint = new IPEndPoint(IPAddress.Parse(protocol.ToString()), localAddress.Get<UDP>().Port);

        QuicServerConnectionOptions serverConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 1,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                ServerCertificate = CertificateHelper.CertificateFromIdentity(_sessionKey, new Identity()),
            },
        };

        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = listenEndPoint,
            ApplicationProtocols = protocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions),
        }, token);

        await using (listener)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    QuicConnection connection = await listener.AcceptConnectionAsync(token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                QuicStream stream = await connection.AcceptInboundStreamAsync(token);
                                IPEndPoint remote = connection.RemoteEndPoint;
                                var remoteAddress = new Multiaddress()
                                    .Add<IP4>(remote.Address.MapToIPv4())
                                    .Add<UDP>(remote.Port)
                                    .Add<QUICv1>();
                                onConnection(new StreamChannel(stream), remoteAddress);
                            }
                        }
                        catch (Exception)
                        {
                            // The connection closed; its streams are done.
                        }
                    }, token);
                }
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessStreams(QuicConnection connection, IRemotePeerConnection peerConnection, IChannelFactory channelFactory, CancellationToken token = default)
    {
        _logger?.LogDebug("New connection to {remote}", connection.RemoteEndPoint);

        bool isIP4 = connection.LocalEndPoint.AddressFamily == AddressFamily.InterNetwork;

        Multiaddress localEndPointMultiaddress = new();
        string strLocalEndpointAddress = connection.LocalEndPoint.Address.ToString();
        localEndPointMultiaddress = isIP4 ? localEndPointMultiaddress.Add<IP4>(strLocalEndpointAddress) : localEndPointMultiaddress.Add<IP6>(strLocalEndpointAddress);
        localEndPointMultiaddress = localEndPointMultiaddress.Add<UDP>(connection.LocalEndPoint.Port);

        peerConnection.LocalEndpoint = localEndPointMultiaddress;

        IPEndPoint remoteIpEndpoint = connection.RemoteEndPoint!;
        isIP4 = remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork;

        Multiaddress remoteEndPointMultiaddress = new();
        string strRemoteEndpointAddress = remoteIpEndpoint.Address.ToString();
        remoteEndPointMultiaddress = isIP4 ? remoteEndPointMultiaddress.Add<IP4>(strRemoteEndpointAddress) : remoteEndPointMultiaddress.Add<IP6>(strRemoteEndpointAddress);
        remoteEndPointMultiaddress = remoteEndPointMultiaddress.Add<UDP>(remoteIpEndpoint.Port);

        peerConnection.RemoteEndpoint = remoteEndPointMultiaddress;

        peerConnection.Connected();

        _ = Task.Run(async () =>
        {
            foreach (IChannelRequest request in peerConnection.SubDialRequests.GetConsumingEnumerable())
            {
                QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                IChannel upChannel = channelFactory.SubDialForRequest(peerConnection, request);
                ExchangeData(stream, upChannel, request.CompletionSource);
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            QuicStream inboundStream = await connection.AcceptInboundStreamAsync(token);
            IChannel upChannel = channelFactory.SubListen(peerConnection);
            ExchangeData(inboundStream, upChannel, null);
        }
    }

    private void ExchangeData(QuicStream stream, IChannel upChannel, TaskCompletionSource? tcs)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            stream.Close();
            tcs?.SetResult();
            _logger?.LogDebug("Stream {stream id}: Closed", stream.Id);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    await stream.WriteAsync(data.ToArray());
                }
                stream.CompleteWrites();
            }
            catch (SocketException ex)
            {
                _logger?.SocketException(ex, ex.Message);
                await upChannel.CloseAsync();
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                while (stream.CanRead)
                {
                    byte[] buf = new byte[1024];
                    int len = await stream.ReadAtLeastAsync(buf, 1, false);
                    if (len != 0)
                    {
                        await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]));
                    }
                }
                await upChannel.WriteEofAsync();
            }
            catch (SocketException ex)
            {
                _logger?.SocketException(ex, ex.Message);
                await upChannel.CloseAsync();
            }
        });
    }
}
