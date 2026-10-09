// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     Assembles a peer factory: protocols are registered flat, by role,
///     into the registry the switch will read — there is no protocol stack
///     definition. Channel-tier protocols are added with
///     <see cref="AddAppLayerProtocol{TProtocol}"/>; managed-tier protocols
///     are DI services, optionally tracked here by their typed ref.
/// </summary>
public interface IPeerFactoryBuilder
{
    IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol;

    IPeerFactoryBuilder AddManagedProtocol<TProtocol, TOut>(ProtocolRef<TProtocol, TOut> protocolRef) where TProtocol : IProtocol<TOut>;

    IEnumerable<IProtocol> AppLayerProtocols { get; }

    IPeerFactory Build();
}
