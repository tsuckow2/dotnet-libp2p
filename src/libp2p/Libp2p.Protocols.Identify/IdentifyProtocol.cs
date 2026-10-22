// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
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

        // The identity is settled, so the rest of the message can be
        // taken as this peer's word: its listen addresses enter its
        // book as self-claims, and the address it observes us at is
        // a vouch for our public form (recorded as an observation;
        // the advertised set decides when enough peers agree).
        string peerId = connection.RemotePeer.Identity.PeerId.ToString();
        foreach (ByteString claimedBytes in identity.ListenAddrs)
        {
            try
            {
                Multiaddress claimed = MultiaddressWire.Decode(claimedBytes.ToByteArray());
                connection.RemotePeer.GetOrAddByValue(claimed)
                    .RecordVouch(peerId, AddressSource.SelfClaimed);
            }
            catch (Exception e) when (e is FormatException or NotSupportedException)
            {
                _logger?.LogDebug("Unparsable listen address in Identify from {peer}", peerId);
            }
        }

        if (identity.ObservedAddr is { IsEmpty: false } observedBytes)
        {
            try
            {
                Multiaddress observed = MultiaddressWire.Decode(observedBytes.ToByteArray());
                connection.LocalPeer.RecordObservedAddress(observed, peerId);
            }
            catch (Exception e) when (e is FormatException or NotSupportedException)
            {
                _logger?.LogDebug("Unparsable observed address in Identify from {peer}", peerId);
            }
        }
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IRemotePeerConnection connection)
    {
        _logger?.LogInformation("Listen");

        // Advertise the book's earned set (the primary address's
        // connection endpoint only when nothing has been earned
        // yet), with the peer id left to the PublicKey field where
        // the protocol carries it, and report the address we
        // observe the dialer at — its public form as we see it.
        IReadOnlyList<Multiaddress> advertised = connection.LocalPeer.AdvertisedAddresses();
        IEnumerable<Multiaddress> listenAddrs = advertised.Count > 0
            ? advertised.Select(WithoutPeerId)
            : [connection.LocalEndpoint!];

        Identify.Dto.Identify identify = new()
        {
            ProtocolVersion = _protocolVersion,
            AgentVersion = _agentVersion,
            PublicKey = connection.LocalPeer.Identity.PublicKey.ToByteString(),
            ListenAddrs = { listenAddrs.Select(addr => ByteString.CopyFrom(MultiaddressWire.Encode(addr))) },
            ObservedAddr = ByteString.CopyFrom(MultiaddressWire.Encode(connection.RemoteEndpoint!)),
            Protocols = { PeerFactoryBuilder.AppLayerProtocols.Select(p => p.Id) }
        };
        byte[] ar = new byte[identify.CalculateSize()];
        identify.WriteTo(ar);

        await channel.WriteSizeAndDataAsync(ar);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }

    private static Multiaddress WithoutPeerId(Multiaddress address)
    {
        string form = address.ToString();
        int lastSlash = form.LastIndexOf('/');
        int protocolSlash = lastSlash > 0 ? form.LastIndexOf('/', lastSlash - 1) : -1;
        return protocolSlash > 0 && form[(protocolSlash + 1)..lastSlash] == "p2p"
            ? Multiaddress.Decode(form[..protocolSlash])
            : address;
    }
}
