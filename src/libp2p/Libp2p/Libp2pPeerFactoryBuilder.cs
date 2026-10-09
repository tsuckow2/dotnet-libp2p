// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p.Stack;

/// <summary>
///     The default stack, registered flat by role — the same protocols the
///     old ProtocolStack tree composed with Over/Or:
///     IpTcp transport, Noise (or Plaintext) security, Yamux muxer,
///     Multistream selector, Identify, and the pubsub protocols as
///     app-layer. Quic is not registered yet: it still speaks the
///     channel-tier transport shape and needs its ITransport port.
/// </summary>
public class Libp2pPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder>,
    ILibp2pPeerFactoryBuilder
{
    public Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : base(serviceProvider)
    {
        AddTransport<IpTcpProtocol>();
        AddSecurityProtocol<NoiseProtocol>();
        AddMuxerProtocol<YamuxProtocol>();
        AddIdentityProtocol<IdentifyProtocol>();
        AddAppLayerProtocol<MultistreamProtocol>();
        AddAppLayerProtocol<GossipsubProtocol>();
        AddAppLayerProtocol<FloodsubProtocol>();
    }

    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced()
    {
        ClearSecurityProtocols();
        AddSecurityProtocol<PlainTextProtocol>();
        return this;
    }
}
