// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace DataTransferBenchmark;

/// <summary>
///     A builder with no stack at all: just the TCP transport, registered
///     flat. This is the benchmark's control case — and the shape every
///     builder has now that ProtocolStack is gone.
/// </summary>
public class NoStackPeerFactoryBuilder : PeerFactoryBuilderBase<NoStackPeerFactoryBuilder>
{
    public NoStackPeerFactoryBuilder() : base(default)
    {
        AddTransport<IpTcpProtocol>();
    }

    public static NoStackPeerFactoryBuilder Create => new();
}
