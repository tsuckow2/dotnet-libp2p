// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The switch: the component that owns connections for one local peer.
///     It dials through <see cref="IPeerFactory"/> and listens on the local
///     peer's addresses, assembling each connection from the protocols in
///     the <see cref="IProtocolRegistry"/> by role.
/// </summary>
public interface IManagedPeerSwitch : IPeerFactory
{
    Task<IListener> ListenAsync(ILocalPeer localPeer, CancellationToken token = default);
}
