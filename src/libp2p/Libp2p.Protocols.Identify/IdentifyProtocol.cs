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
        IPeerContext context)
    {
        _logger?.LogInformation("Dial");

        Identify.Dto.Identify identity = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser);

        _logger?.LogInformation("Received peer info: {identify}", identity);
        context.RemotePeer.Identity = new Identity(PublicKey.Parser.ParseFrom(identity.PublicKey));

        if (context.RemotePeer.Identity.PublicKey.ToByteString() != identity.PublicKey)
        {
            throw new PeerConnectionException();
        }
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        _logger?.LogInformation("Listen");

        Identify.Dto.Identify identify = new()
        {
            ProtocolVersion = _protocolVersion,
            AgentVersion = _agentVersion,
            PublicKey = context.LocalPeer.Identity.PublicKey.ToByteString(),
            ListenAddrs = { ByteString.CopyFrom(context.LocalEndpoint.ToBytes()) },
            ObservedAddr = ByteString.CopyFrom(context.RemoteEndpoint.ToBytes()),
            Protocols = { PeerFactoryBuilder.AppLayerProtocols.Select(p => p.Id) }
        };
        byte[] ar = new byte[identify.CalculateSize()];
        identify.WriteTo(ar);

        await channel.WriteSizeAndDataAsync(ar);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }
}
