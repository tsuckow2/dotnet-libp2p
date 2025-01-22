// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeerFactoryBuilder
{
    IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol<object>;
    IPeerFactory Build();
    IEnumerable<IProtocol<object>> AppLayerProtocols { get; }
}
