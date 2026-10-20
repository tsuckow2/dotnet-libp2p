// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Noise;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;
using Google.Protobuf.Collections;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// </summary>
public class NoiseProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null) : ISecurityProtocol
{
    private readonly Protocol _protocol = new(
            HandshakePattern.XX,
            CipherFunction.ChaChaPoly,
            HashFunction.Sha256
        );
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<NoiseProtocol>();
    private readonly NoiseExtensions _extensions = new NoiseExtensions()
    {
        StreamMuxers =
        {
            multiplexerSettings is null || multiplexerSettings.Multiplexers.Any() ? ["na"] : [.. multiplexerSettings.Multiplexers.Select(proto => proto.Id)]
        }
    };

    public string Id => "/noise";
    private const string PayloadSigPrefix = "noise-libp2p-static-key:";

    public async Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IRemotePeerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(upChannelFactory);

        KeyPair? clientStatic = KeyPair.Generate();
        using HandshakeState? handshakeState = _protocol.Create(true, s: clientStatic.PrivateKey);
        byte[] buffer = new byte[Protocol.MaxMessageLength];

        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg0 = handshakeState.WriteMessage(null, buffer);

        byte[]? lenBytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg0.BytesWritten);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg0.BytesWritten));

        lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();

        int len = BinaryPrimitives.ReadInt16BigEndian(lenBytes.AsSpan());
        ReadOnlySequence<byte> received = await downChannel.ReadAsync(len).OrThrow();
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.ReadMessage(received.ToArray(), buffer);
        NoiseHandshakePayload? msg1Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg1.BytesRead));
        PublicKey? msg1KeyDecoded = PublicKey.Parser.ParseFrom(msg1Decoded.IdentityKey);
        //var key = new byte[] { 0x1 }.Concat(clientStatic.PublicKey).ToArray();

        if (!EstablishRemoteIdentity(connection, handshakeState, msg1Decoded))
        {
            await connection.DisconnectAsync();
            return;
        }

        byte[] msg = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. ByteString.CopyFrom(clientStatic.PublicKey)];
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = connection.LocalPeer.Identity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(connection.LocalPeer.Identity.Sign(msg)),
            Extensions = _extensions
        };

        if (_logger is not null && _logger.IsEnabled(LogLevel.Trace))
        {
            _logger?.LogTrace("Local public key {0}", Convert.ToHexString(clientStatic.PublicKey));
            //_logger?.LogTrace("local  prv key {0}", clientStatic.PrivateKey);
            _logger?.LogTrace("Remote public key {0}", Convert.ToHexString(handshakeState.RemoteStaticPublicKey.ToArray()));
        }

        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg2 =
            handshakeState.WriteMessage(payload.ToByteArray(), buffer);
        BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg2.BytesWritten);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg2.BytesWritten));
        Transport? transport = msg2.Transport;

        _logger?.LogDebug("Established connection to {peer}", connection.RemotePeer.Address);

        IChannel upChannel = upChannelFactory.SubDial(connection);

        await ExchangeData(transport, downChannel, upChannel);

        _ = upChannel.CloseAsync();
        _logger?.LogDebug("Closed");
    }

    public async Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IRemotePeerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(upChannelFactory);

        KeyPair? serverStatic = KeyPair.Generate();
        using HandshakeState? handshakeState =
            _protocol.Create(false,
                s: serverStatic.PrivateKey);

        byte[]? lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();
        short len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
        byte[] buffer = new byte[Protocol.MaxMessageLength];
        ReadOnlySequence<byte> msg0Bytes = await downChannel.ReadAsync(len).OrThrow();
        handshakeState.ReadMessage(msg0Bytes.ToArray(), buffer);

        byte[] msg = Encoding.UTF8.GetBytes(PayloadSigPrefix)
            .Concat(ByteString.CopyFrom(serverStatic.PublicKey))
            .ToArray();
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = connection.LocalPeer.Identity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(connection.LocalPeer.Identity.Sign(msg)),
            Extensions = _extensions
        };

        // Send the second handshake message to the client.
        buffer = new byte[Protocol.MaxMessageLength];
        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.WriteMessage(payload.ToByteArray(), buffer.AsSpan(2));
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(), (short)msg1.BytesWritten);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg1.BytesWritten + 2));

        lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();
        len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
        ReadOnlySequence<byte> hs2Bytes = await downChannel.ReadAsync(len).OrThrow();
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg2 =
            handshakeState.ReadMessage(hs2Bytes.ToArray(), buffer);
        NoiseHandshakePayload? msg2Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg2.BytesRead));
        PublicKey? msg2KeyDecoded = PublicKey.Parser.ParseFrom(msg2Decoded.IdentityKey);
        Transport? transport = msg2.Transport;

        if (!EstablishRemoteIdentity(connection, handshakeState, msg2Decoded))
        {
            await connection.DisconnectAsync();
            return;
        }

        _logger?.LogDebug("Established connection to {peer}", connection.RemotePeer.Address);

        IChannel upChannel = upChannelFactory.SubListen(connection);

        await ExchangeData(transport, downChannel, upChannel);

        _ = upChannel.CloseAsync();
        _logger?.LogDebug("Closed");
    }

    /// <summary>
    ///     The handshake payload claims an identity key; what proves
    ///     the claim is the payload signature binding that key to the
    ///     handshake's static key — the only thing the Noise exchange
    ///     itself authenticates. Verify the binding, enforce the
    ///     dial's expectation when it named a peer, and establish the
    ///     remote peer's identity from the proven key, so everything
    ///     above the security layer talks to a named, proven peer.
    ///     (Before this, the presented key was taken on trust: the
    ///     signature was never checked, here or anywhere else.)
    ///     Returns false when the claim fails: the caller aborts the
    ///     connection, which is how a stack layer faults an
    ///     establishment in progress — the switch's dial then fails
    ///     as a connection that closed before it was established.
    /// </summary>
    private bool EstablishRemoteIdentity(
        IRemotePeerConnection connection, HandshakeState handshakeState, NoiseHandshakePayload payload)
    {
        PublicKey provenKey = PublicKey.Parser.ParseFrom(payload.IdentityKey);
        var provenIdentity = new Identity(provenKey);

        byte[] binding = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. handshakeState.RemoteStaticPublicKey.ToArray()];
        if (!provenIdentity.VerifySignature(binding, payload.IdentitySig.ToByteArray()))
        {
            _logger?.LogWarning(
                "Aborting the connection to {peer}: the handshake payload's signature does not bind the presented identity {identity} to the negotiated static key.",
                connection.RemotePeer.Address, provenIdentity.PeerId);
            return false;
        }

        if (connection.ExpectedPeerId is { } expected
            && !expected.Bytes.AsSpan().SequenceEqual(provenIdentity.PeerId.Bytes))
        {
            _logger?.LogWarning(
                "Aborting the dial to {peer}: the handshake proved {proven}, not the expected {expected}.",
                connection.RemotePeer.Address, provenIdentity.PeerId, expected);
            return false;
        }

        connection.ProvenPeerId = provenIdentity.PeerId;
        connection.RemotePeer.Identity = provenIdentity;
        if (!connection.RemotePeer.Address.Has<P2P>())
        {
            connection.RemotePeer.Address.Add(new P2P(provenIdentity.PeerId.ToString()));
        }
        return true;
    }

    private static Task ExchangeData(Transport transport, IChannel downChannel, IChannel upChannel)
    {
        // UP -> DOWN
        Task t = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadResult dataReadResult = await upChannel.ReadAsync(Protocol.MaxMessageLength - 16, ReadBlockingMode.WaitAny);
                if (dataReadResult.Result != IOResult.Ok)
                {
                    return;
                }

                byte[] buffer = new byte[2 + 16 + dataReadResult.Data.Length];

                int bytesWritten = transport.WriteMessage(dataReadResult.Data.ToArray(), buffer.AsSpan(2));
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(), (ushort)bytesWritten);
                IOResult writeResult = await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer));
                if (writeResult != IOResult.Ok)
                {
                    return;
                }
            }
        });
        // DOWN -> UP
        Task t2 = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadResult lengthBytesReadResult = await downChannel.ReadAsync(2, ReadBlockingMode.WaitAll);
                if (lengthBytesReadResult.Result != IOResult.Ok)
                {
                    return;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytesReadResult.Data.ToArray().AsSpan());

                ReadResult dataReadResult = await downChannel.ReadAsync(length);
                if (dataReadResult.Result != IOResult.Ok)
                {
                    return;
                }
                byte[] buffer = new byte[length - 16];

                int bytesRead = transport.ReadMessage(dataReadResult.Data.ToArray(), buffer);

                IOResult writeResult = await upChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, bytesRead));
                if (writeResult != IOResult.Ok)
                {
                    return;
                }
            }
        });

        return Task.WhenAny(t, t2).ContinueWith((t) =>
        {

        });
    }
}
