// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The registry backed by DI: a role collection is simply every service
///     registered under that role's interface.
/// </summary>
public class DIProtocolRegistry(IServiceProvider serviceProvider) : IProtocolRegistry
{
    public IEnumerable<ITransport> Transports => serviceProvider.GetService<IEnumerable<ITransport>>() ?? [];
    public IEnumerable<IMuxerProtocol> MuxerProtocols => serviceProvider.GetService<IEnumerable<IMuxerProtocol>>() ?? [];
    public IEnumerable<ISecurityProtocol> SecurityProtocols => serviceProvider.GetService<IEnumerable<ISecurityProtocol>>() ?? [];
    public IEnumerable<IIdentityProtocol> IdentityProtocols => serviceProvider.GetService<IEnumerable<IIdentityProtocol>>() ?? [];
    public IEnumerable<IProtocol> OtherProtocols => serviceProvider.GetService<IEnumerable<IProtocol>>() ?? [];
}
