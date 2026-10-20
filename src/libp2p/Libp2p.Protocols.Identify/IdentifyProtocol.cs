// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/identify
/// </summary>
public class IdentifyProtocol : IIdentityProtocol
{
    private readonly string _agentVersion;
    private readonly string _protocolVersion;

    private readonly ILogger? _logger;
    private readonly IServiceProvider _serviceProvider;
    private IPeerFactoryBuilder? _peerFactoryBuilder;

    public IdentifyProtocol(IServiceProvider serviceProvider, IdentifyProtocolSettings? settings = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<IdentifyProtocol>();
        _serviceProvider = serviceProvider;

        _agentVersion = settings?.AgentVersion ?? IdentifyProtocolSettings.Default.AgentVersion!;
        _protocolVersion = settings?.ProtocolVersion ?? IdentifyProtocolSettings.Default.ProtocolVersion!;
    }

    // The builder creates this protocol, so it can only be resolved lazily,
    // once construction has finished; resolving it in the constructor
    // deadlocks the DI scope that is building the stack.
    private IPeerFactoryBuilder PeerFactoryBuilder
    {
        get => _peerFactoryBuilder ??= _serviceProvider.GetRequiredService<IPeerFactoryBuilder>();
    }

    public string Id => "/ipfs/id/1.0.0";

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory,
        IRemotePeerConnection connection)
    {
        _logger?.LogInformation("Dial");

        Identify.Dto.Identify identity = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser);

        _logger?.LogInformation("Received peer info: {identify}", identity);

        // Identify's word is not proof: when the security layer has
        // already proven who this peer is, a message naming a
        // different key is a lie about the connection's other end —
        // refuse it rather than rename a proven peer. (With no
        // security layer in the stack there is no proof, and Identify
        // remains the naming authority, as before.)
        PublicKey identifiedKey = PublicKey.Parser.ParseFrom(identity.PublicKey);
        if (connection.ProvenPeerId is { } proven
            && !proven.Bytes.AsSpan().SequenceEqual(new PeerId(identifiedKey).Bytes))
        {
            throw new PeerConnectionException(
                $"Identify named {new PeerId(identifiedKey)}, but the security layer proved {proven}.");
        }

        connection.RemotePeer.Identity = new Identity(identifiedKey);

        if (connection.RemotePeer.Identity.PublicKey.ToByteString() != identity.PublicKey)
        {
            throw new PeerConnectionException();
        }
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IRemotePeerConnection connection)
    {
        _logger?.LogInformation("Listen");

        Identify.Dto.Identify identify = new()
        {
            ProtocolVersion = _protocolVersion,
            AgentVersion = _agentVersion,
            PublicKey = connection.LocalPeer.Identity.PublicKey.ToByteString(),
            ListenAddrs = { ByteString.CopyFrom(connection.LocalEndpoint!.ToBytes()) },
            ObservedAddr = ByteString.CopyFrom(connection.RemoteEndpoint!.ToBytes()),
            Protocols = { PeerFactoryBuilder.AppLayerProtocols.Select(p => p.Id) }
        };
        byte[] ar = new byte[identify.CalculateSize()];
        identify.WriteTo(ar);

        await channel.WriteSizeAndDataAsync(ar);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }
}
