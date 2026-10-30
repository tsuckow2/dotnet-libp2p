// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Circuit;
using Nethermind.Libp2p.Protocols.Dcutr;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p.Stack;

/// <summary>
///     The default stack, registered flat by role — the same protocols the
///     old ProtocolStack tree composed with Over/Or:
///     IpTcp and Quic transports, Noise (or Plaintext) security, Yamux
///     muxer, Multistream selector, Identify, and the pubsub protocols
///     as app-layer. The switch picks the transport by address; Quic
///     carries its own TLS and stream multiplexing, so above it the
///     stack is only the selector and the app protocols.
/// </summary>
public class Libp2pPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder>,
    ILibp2pPeerFactoryBuilder
{
    public Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : base(serviceProvider)
    {
        AddTransport<IpTcpProtocol>();
        AddTransport<QuicheTransport>();
        AddTransport<CircuitTransport>();
        AddSecurityProtocol<NoiseProtocol>();
        AddMuxerProtocol<YamuxProtocol>();
        AddIdentityProtocol<IdentifyProtocol>();
        AddAppLayerProtocol<MultistreamProtocol>();
        AddAppLayerProtocol<GossipsubProtocol>();
        AddAppLayerProtocol<FloodsubProtocol>();
        AddAppLayerProtocol<HopProtocol>();
        AddAppLayerProtocol<StopProtocol>();
        AddAppLayerProtocol<DcutrProtocol>();
    }

    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced()
    {
        ClearSecurityProtocols();
        AddSecurityProtocol<PlainTextProtocol>();
        return this;
    }
}
