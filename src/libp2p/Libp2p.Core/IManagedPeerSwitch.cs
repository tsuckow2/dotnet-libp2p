// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

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

    /// <summary>
    ///     Create a local peer attached to this switch, so its
    ///     connection-bound surface (DialAsync/ListenAsync) delegates here.
    /// </summary>
    ILocalPeer Create(Identity? identity = null, Multiaddress? localAddr = null);

    /// <summary>
    ///     The switch's book entry for a remote identity, created on first
    ///     use: the peer information (identity + known addresses) that
    ///     DialAsync(IRemotePeer) dials across.
    /// </summary>
    IRemotePeer GetRemotePeer(Identity identity);
}
