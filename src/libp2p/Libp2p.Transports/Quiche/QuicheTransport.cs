// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Quiche;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// A libp2p transport over Cloudflare quiche, via the Quiche.NET
/// managed API. Standalone ITransport path: dials and listens at the
/// address level and hands up one raw pipe (a StreamChannel over a
/// quiche stream) per connection — the same contract
/// IpTcpProtocol's ITransport methods offer.
/// Identity: the standalone path presents an ephemeral identity and
/// accepts any libp2p certificate, exactly like QuicProtocol's
/// standalone methods; identity binding is left to the layers above
/// (the stack path, Phase 4, binds the dial-side certificate proof
/// to ProvenPeerId and the punch nonce for inbound).
/// Sockets: a transport instance holds at most two QuicSockets. The
/// listen socket is created by ListenAsync and is shared: dials made
/// after listening go out on the listener's socket, so the source
/// port is the advertised listen port — the DCUtR prerequisite
/// msquic could not deliver. A dial made before any listen creates
/// a separate ephemeral dial socket; a later listen still creates
/// its own listen socket (earlier dials stay on the dial socket).
/// Listen-first is the normal node order and the only order the
/// shared-port guarantee covers.
/// Certificates: quiche's C API loads server certificates from PEM
/// files only (no in-memory loader at 0.30.0), so the ephemeral
/// identity's certificate and key are written to temp files under
/// one directory per transport instance, deleted on dispose.
/// IPv4 only, like the Quiche.NET socket underneath.
/// </summary>
public partial class QuicheTransport : ITransport, IProtocol, IDisposable
{
    private readonly ILogger<QuicheTransport>? _logger;
    private readonly ECDsa _sessionKey;
    private readonly object _gate = new();
    private QuicSocket? _listenSocket;
    private QuicSocket? _dialSocket;
    private string? _certDir;
    private bool _disposed;

    /// <summary>libp2p's QUIC ALPN, as in the msquic transport.</summary>
    public const string Alpn = "libp2p";

    /// <summary>How long a dial waits for the handshake before failing.</summary>
    public static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);

    public QuicheTransport(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<QuicheTransport>();
        _sessionKey = ECDsa.Create();
    }

    public string AddressProtocol => "quic-v1";

    public async Task<IRemoteComm> DialAsync(Multiaddress address)
    {
        IPAddress ipAddress = await MultiaddressEndpoint.ResolveIpAsync(address);
        int udpPort = address.Get<UDP>().Port;
        var remoteEndPoint = new IPEndPoint(ipAddress, udpPort);

        _logger?.LogDebug("Dialing {0}:{1}", ipAddress, udpPort);

        QuicSocket socket = GetDialSocket();
        using var timeout = new CancellationTokenSource(DialTimeout);
        QuicConnection connection = await socket.ConnectAsync(remoteEndPoint, ipAddress.ToString(), new QuicClientOptions
        {
            Alpn = Alpn,
            VerifyPeer = false,
        }, timeout.Token);

        QuicStream stream = connection.OpenStream();
        var transportConnection = new GenericRemoteConnection(address, () =>
        {
            connection.Close();
            return Task.CompletedTask;
        });
        return new GenericRemoteComm(false, new StreamChannel(new QuicStreamAdapter(stream)), transportConnection);
    }

    public async Task ListenAsync(Multiaddress localAddress, Action<IChannel, Multiaddress> onConnection, CancellationToken token)
    {
        MultiaddressProtocol ipProtocol = localAddress.Has<IP4>() ? localAddress.Get<IP4>() : localAddress.Get<IP6>();
        var listenEndPoint = new IPEndPoint(IPAddress.Parse(ipProtocol.ToString()), localAddress.Get<UDP>().Port);

        QuicSocket socket = GetListenSocket(listenEndPoint);
        try
        {
            while (!token.IsCancellationRequested)
            {
                QuicConnection connection = await socket.AcceptAsync(token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            QuicStream stream = await connection.AcceptStreamAsync(token);
                            IPEndPoint remote = connection.RemoteEndPoint;
                            var remoteAddress = new Multiaddress()
                                .Add<IP4>(remote.Address.MapToIPv4())
                                .Add<UDP>(remote.Port)
                                .Add<QUICv1>();
                            onConnection(new StreamChannel(new QuicStreamAdapter(stream)), remoteAddress);
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
        finally
        {
            // Listening owns the listen socket: shutting down
            // listening retires it so a later dial starts fresh.
            // (A dial made before this listen keeps its own socket.)
            DisposeListenSocket();
        }
    }

    private QuicSocket GetListenSocket(IPEndPoint listenEndPoint)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_listenSocket is not null)
            {
                if (!_listenSocket.LocalEndPoint.Equals(listenEndPoint))
                {
                    throw new InvalidOperationException(
                        $"Transport is already listening on {_listenSocket.LocalEndPoint}, cannot also listen on {listenEndPoint}.");
                }
                return _listenSocket;
            }
            string certDir = GetOrCreateCertFiles();
            _listenSocket = new QuicSocket(listenEndPoint, new QuicServerOptions
            {
                Alpn = Alpn,
                CertificateFile = Path.Combine(certDir, "cert.pem"),
                PrivateKeyFile = Path.Combine(certDir, "key.pem"),
            });
            return _listenSocket;
        }
    }

    private QuicSocket GetDialSocket()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_listenSocket is not null)
            {
                return _listenSocket;
            }
            _dialSocket ??= new QuicSocket(new IPEndPoint(IPAddress.Any, 0));
            return _dialSocket;
        }
    }

    private void DisposeListenSocket()
    {
        lock (_gate)
        {
            _listenSocket?.Dispose();
            _listenSocket = null;
        }
    }

    private string GetOrCreateCertFiles()
    {
        if (_certDir is not null)
        {
            return _certDir;
        }
        X509Certificate cert = CertificateHelper.CertificateFromIdentity(_sessionKey, new Identity());
        string certPem = new X509Certificate2(cert.GetRawCertData()).ExportCertificatePem();
        string keyPem = _sessionKey.ExportPkcs8PrivateKeyPem();
        string dir = Path.Combine(Path.GetTempPath(), "quiche-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cert.pem"), certPem);
        File.WriteAllText(Path.Combine(dir, "key.pem"), keyPem);
        _certDir = dir;
        return dir;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _listenSocket?.Dispose();
            _listenSocket = null;
            _dialSocket?.Dispose();
            _dialSocket = null;
            _sessionKey.Dispose();
        }
        if (_certDir is not null)
        {
            try
            {
                Directory.Delete(_certDir, recursive: true);
            }
            catch
            {
                // Best effort; the OS reclaims temp files.
            }
        }
    }
}
