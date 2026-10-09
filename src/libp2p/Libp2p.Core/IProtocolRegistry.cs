// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     Every protocol available to the switch, categorized by role. This is
///     the single registry (D2): the builder registers into it, the switch
///     reads from it, and nothing assembles a protocol stack anywhere else.
/// </summary>
public interface IProtocolRegistry
{
    IEnumerable<ITransport> Transports { get; }

    IEnumerable<IMuxerProtocol> MuxerProtocols { get; }

    IEnumerable<ISecurityProtocol> SecurityProtocols { get; }

    IEnumerable<IIdentityProtocol> IdentityProtocols { get; }

    IEnumerable<IProtocol> OtherProtocols { get; }
}
