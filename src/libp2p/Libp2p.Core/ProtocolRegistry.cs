// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A registry over plain lists — what a builder hands to the switch for
///     the protocols it registered itself.
/// </summary>
internal class InMemoryProtocolRegistry : IProtocolRegistry
{
    public IEnumerable<ITransport> Transports { get; init; } = [];
    public IEnumerable<IMuxerProtocol> MuxerProtocols { get; init; } = [];
    public IEnumerable<ISecurityProtocol> SecurityProtocols { get; init; } = [];
    public IEnumerable<IIdentityProtocol> IdentityProtocols { get; init; } = [];
    public IEnumerable<IProtocol> OtherProtocols { get; init; } = [];
}

/// <summary>
///     Merges two registries; on duplicates the first registry wins.
/// </summary>
internal class CompositeProtocolRegistry(IProtocolRegistry first, IProtocolRegistry second) : IProtocolRegistry
{
    public IEnumerable<ITransport> Transports => first.Transports.Concat(second.Transports).Distinct();
    public IEnumerable<IMuxerProtocol> MuxerProtocols => first.MuxerProtocols.Concat(second.MuxerProtocols).Distinct();
    public IEnumerable<ISecurityProtocol> SecurityProtocols => first.SecurityProtocols.Concat(second.SecurityProtocols).Distinct();
    public IEnumerable<IIdentityProtocol> IdentityProtocols => first.IdentityProtocols.Concat(second.IdentityProtocols).Distinct();
    public IEnumerable<IProtocol> OtherProtocols => first.OtherProtocols.Concat(second.OtherProtocols).Distinct();
}
