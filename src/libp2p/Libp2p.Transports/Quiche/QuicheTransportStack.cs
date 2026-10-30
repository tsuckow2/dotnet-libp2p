// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Quiche;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// The stack path (IProtocol) for QuicheTransport: the transport as
/// the switch sees it. Mirrors QuicProtocol's stack methods, with
/// two quiche-specific differences:
///   * Dial-side identity proof is post-hoc, not a handshake
///     callback (quiche has none): after ConnectAsync, the peer's
///     certificate is read via PeerCertificate and validated with
///     CertificateHelper exactly as msquic's validation callback
///     validates it. A missing or invalid certificate closes the
///     connection and fails the dial — an unauthenticated
///     connection never flows up.
///   * Inbound identity comes from a dedicated first stream
///     carrying a QuicheProofFrame (the listener has no client
///     certificate to read). The accept loop peeks at the first
///     stream: a proof frame is consumed and validated (invalid
///     proofs close the connection); anything else is handed up
///     with the peeked bytes replayed, unproven, so other QUIC
///     implementations keep working and Identify names them.
/// The dial sends its proof stream before any upper-layer stream,
/// so the listener always sees it first.
/// </summary>
public partial class QuicheTransport
{
    public string Id => "quic-v1";

    public async Task ListenAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection peerConnection)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException($"The protocol requires {nameof(channelFactory)}");
        }

        Multiaddress addr = peerConnection.LocalPeer.Address;
        MultiaddressProtocol ipProtocol = addr.Has<IP4>() ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int udpPort = int.Parse(addr.Get<UDP>().ToString());

        var localEndpoint = new IPEndPoint(ipAddress, udpPort);
        // The stack path's server certificate must embed the local
        // peer's identity: the dial side validates it against the
        // expected peer, and an ephemeral identity would fail that
        // check (and misname anonymous dials).
        QuicSocket socket = GetListenSocket(localEndpoint, peerConnection.LocalPeer.Identity);

        var localEndPoint = new Multiaddress();
        string strLocalEndpoint = socket.LocalEndPoint.Address.ToString();
        localEndPoint = addr.Has<IP4>() ? localEndPoint.Add<IP4>(strLocalEndpoint) : localEndPoint.Add<IP6>(strLocalEndpoint);
        localEndPoint = localEndPoint.Add<UDP>(socket.LocalEndPoint.Port);
        peerConnection.LocalEndpoint = localEndPoint;

        if (udpPort == 0)
        {
            peerConnection.LocalPeer.Address = peerConnection.LocalPeer.Address
                .ReplaceOrAdd<UDP>(socket.LocalEndPoint.Port);
        }
        peerConnection.LocalPeer.ExpandWildcardListener(peerConnection.LocalPeer.Address);

        _logger?.LogDebug("Ready to handle connections.");
        peerConnection.ListenerReady();

        using var listenCts = new CancellationTokenSource();
        singalingChannel.GetAwaiter().OnCompleted(() => listenCts.Cancel());
        try
        {
            while (!listenCts.IsCancellationRequested)
            {
                QuicConnection connection;
                try
                {
                    connection = await socket.AcceptAsync(listenCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        IRemotePeerConnection accepted = channelFactory.AcceptConnection();
                        (QuicStream? firstStream, byte[]? firstPrefix, bool proceed) =
                            await AcceptFirstStreamAsync(connection, accepted, listenCts.Token);
                        if (!proceed)
                        {
                            // Shutdown, or a confirmed proof stream
                            // that failed validation (the connection
                            // is closed): nothing is handed up.
                            return;
                        }
                        await ProcessStreams(connection, accepted, channelFactory, firstStream, firstPrefix, listenCts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("Closed with exception {exception}", ex.Message);
                    }
                }, listenCts.Token);
            }
        }
        finally
        {
            DisposeListenSocket();
        }
    }

    public async Task DialAsync(IChannel singalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection peerConnection)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException($"The protocol requires {nameof(channelFactory)}");
        }

        Multiaddress addr = peerConnection.RemotePeer.Address;
        IPAddress ipAddress = await MultiaddressEndpoint.ResolveIpAsync(addr);
        int udpPort = int.Parse(addr.Get<UDP>().ToString());
        var remoteEndpoint = new IPEndPoint(ipAddress, udpPort);

        QuicSocket socket = GetDialSocket();
        QuicConnection connection = await socket.ConnectAsync(remoteEndpoint, ipAddress.ToString(), new QuicClientOptions
        {
            Alpn = Alpn,
            VerifyPeer = false,
        });

        // Dial-side proof, post-hoc: quiche has no validation
        // callback, so the handshake ran with verification off and
        // the certificate is readable now. This is the same check
        // msquic runs during its handshake, with the same outcome
        // on failure — the connection closes and the dial fails.
        byte[]? der = connection.PeerCertificate;
        Identity? provenIdentity = null;
        if (der is null
            || !CertificateHelper.ValidateCertificate(new X509Certificate2(der), peerConnection.ExpectedPeerId?.ToString(), out provenIdentity)
            || provenIdentity is null)
        {
            connection.Close();
            throw new InvalidOperationException(
                $"QUIC dial to {addr}: peer did not prove a libp2p identity (no certificate, or validation failed).");
        }
        peerConnection.ProvenPeerId = provenIdentity.PeerId;
        peerConnection.RemotePeer.Identity = provenIdentity;

        _logger?.LogDebug("Connected {local} {remote}", connection.LocalEndPoint, connection.RemoteEndPoint);

        // The proof stream goes first, before any upper-layer
        // stream, so the listener's first accepted stream is it.
        byte[] frame = QuicheProofFrame.Encode(peerConnection.LocalPeer.Identity, provenIdentity.PeerId);
        QuicStream proofStream = connection.OpenStream();
        await proofStream.SendAsync(frame, fin: true);

        singalingChannel.GetAwaiter().OnCompleted(() => connection.Close());

        await ProcessStreams(connection, peerConnection, channelFactory, firstStream: null, firstPrefix: null);
    }

    /// <summary>
    /// Accepts the first stream of an inbound connection and decides
    /// what it is. Returns (firstStream, firstPrefix, proceed):
    /// proceed is false only for shutdown or a confirmed proof
    /// stream that failed validation (the connection is closed) —
    /// the caller hands up nothing then. Otherwise the caller runs
    /// ProcessStreams, with the first stream (and an optional prefix
    /// of already-read bytes to replay) or with neither when the
    /// proof stream was valid and consumed.
    /// </summary>
    private async Task<(QuicStream? FirstStream, byte[]? FirstPrefix, bool Proceed)> AcceptFirstStreamAsync(
        QuicConnection connection, IRemotePeerConnection accepted, CancellationToken token)
    {
        using var proofCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        proofCts.CancelAfter(TimeSpan.FromSeconds(10));
        CancellationToken proofToken = proofCts.Token;

        QuicStream stream;
        try
        {
            stream = await connection.AcceptStreamAsync(proofToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or no stream promptly: later streams are
            // normal ones. The connection itself is healthy.
            return (null, null, !token.IsCancellationRequested);
        }
        catch (Exception)
        {
            // The connection went away before its first stream.
            connection.Close();
            return (null, null, false);
        }

        // Peek: accumulate until the bytes decide — magic confirmed,
        // diverged from the magic, FIN, or timeout.
        using var peeked = new MemoryStream();
        bool magicConfirmed = false;
        bool diverged = false;
        bool finSeen = false;
        while (!finSeen && !diverged)
        {
            QuicReadResult result;
            try
            {
                result = await stream.ReadAsync(proofToken);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    return (null, null, false);
                }
                break;
            }
            catch (Exception)
            {
                // The stream died mid-peek: hand up what was read;
                // the pump will see the failure next.
                break;
            }
            if (result.Data.Length > 0)
            {
                peeked.Write(result.Data);
            }
            if (result.Fin)
            {
                finSeen = true;
            }
            byte[] soFar = peeked.ToArray();
            if (soFar.Length >= QuicheProofFrame.Magic.Length)
            {
                if (soFar.AsSpan(0, QuicheProofFrame.Magic.Length).SequenceEqual(QuicheProofFrame.Magic))
                {
                    magicConfirmed = true;
                }
                else
                {
                    diverged = true;
                }
            }
            else if (soFar.Length > 0
                && !QuicheProofFrame.Magic.AsSpan(0, soFar.Length).SequenceEqual(soFar))
            {
                diverged = true;
            }
            if (magicConfirmed && finSeen)
            {
                break;
            }
        }

        if (magicConfirmed)
        {
            if (!finSeen)
            {
                // Confirmed proof stream that never completed
                // (timeout): an incomplete proof is invalid.
                connection.Close();
                return (null, null, false);
            }
            Identity? proven = QuicheProofFrame.Validate(peeked.ToArray(), accepted.LocalPeer.Identity.PeerId);
            if (proven is null)
            {
                connection.Close();
                return (null, null, false);
            }
            accepted.ProvenPeerId = proven.PeerId;
            accepted.RemotePeer.Identity = proven;
            // The proof stream is consumed, not handed up.
            return (null, null, true);
        }

        // Not a proof stream (diverged, FIN without magic, stream
        // died, or the peek timed out undecided): hand it up with
        // the peeked bytes replayed, unproven. A null prefix when
        // nothing was read is the same stream.
        byte[] prefix = peeked.ToArray();
        return (stream, prefix.Length == 0 ? null : prefix, true);
    }

    private async Task ProcessStreams(QuicConnection connection, IRemotePeerConnection peerConnection, IChannelFactory channelFactory, QuicStream? firstStream, byte[]? firstPrefix, CancellationToken token = default)
    {
        bool isIP4 = connection.LocalEndPoint.AddressFamily == AddressFamily.InterNetwork;

        Multiaddress localEndPointMultiaddress = new();
        string strLocalEndpointAddress = connection.LocalEndPoint.Address.ToString();
        localEndPointMultiaddress = isIP4 ? localEndPointMultiaddress.Add<IP4>(strLocalEndpointAddress) : localEndPointMultiaddress.Add<IP6>(strLocalEndpointAddress);
        localEndPointMultiaddress = localEndPointMultiaddress.Add<UDP>(connection.LocalEndPoint.Port);
        peerConnection.LocalEndpoint = localEndPointMultiaddress;

        IPEndPoint remoteIpEndpoint = connection.RemoteEndPoint;
        isIP4 = remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork;

        Multiaddress remoteEndPointMultiaddress = new();
        string strRemoteEndpointAddress = remoteIpEndpoint.Address.ToString();
        remoteEndPointMultiaddress = isIP4 ? remoteEndPointMultiaddress.Add<IP4>(strRemoteEndpointAddress) : remoteEndPointMultiaddress.Add<IP6>(strRemoteEndpointAddress);
        remoteEndPointMultiaddress = remoteEndPointMultiaddress.Add<UDP>(remoteIpEndpoint.Port);
        peerConnection.RemoteEndpoint = remoteEndPointMultiaddress;

        peerConnection.Connected();

        _ = Task.Run(() =>
        {
            foreach (IChannelRequest request in peerConnection.SubDialRequests.GetConsumingEnumerable())
            {
                QuicStream stream = connection.OpenStream();
                IChannel upChannel = channelFactory.SubDialForRequest(peerConnection, request);
                ExchangeData(stream, upChannel, request.CompletionSource);
            }
        }, token);

        if (firstStream is not null)
        {
            IChannel upChannel = channelFactory.SubListen(peerConnection);
            ExchangeData(firstStream, upChannel, null, firstPrefix);
        }

        while (!token.IsCancellationRequested)
        {
            QuicStream inboundStream = await connection.AcceptStreamAsync(token);
            IChannel upChannel = channelFactory.SubListen(peerConnection);
            ExchangeData(inboundStream, upChannel, null);
        }
    }

    private void ExchangeData(QuicStream stream, IChannel upChannel, TaskCompletionSource? tcs, byte[]? prefix = null)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            _ = FinishStreamAsync(stream);
            tcs?.SetResult();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    await stream.SendAsync(data.ToArray());
                }
                await stream.SendAsync([], fin: true);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Up-pump closed: {exception}", ex.Message);
                await upChannel.CloseAsync();
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (prefix is not null)
                {
                    await upChannel.WriteAsync(new ReadOnlySequence<byte>(prefix));
                }
                while (true)
                {
                    QuicReadResult result = await stream.ReadAsync();
                    if (result.Data.Length > 0)
                    {
                        await upChannel.WriteAsync(new ReadOnlySequence<byte>(result.Data));
                    }
                    if (result.Fin)
                    {
                        break;
                    }
                }
                await upChannel.WriteEofAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Down-pump closed: {exception}", ex.Message);
                await upChannel.CloseAsync();
            }
        });
    }

    private static async Task FinishStreamAsync(QuicStream stream)
    {
        try
        {
            await stream.SendAsync([], fin: true);
        }
        catch
        {
            // Already closed; nothing to finish.
        }
    }
}
