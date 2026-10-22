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
    /// <summary>
    ///     The local peer this switch owns connections for.
    /// </summary>
    ILocalPeer LocalPeer { get; }

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

    /// <summary>
    ///     Every remote peer the switch holds a book for, by identity.
    /// </summary>
    IReadOnlyDictionary<Identity, IRemotePeer> RemotePeers { get; }

    /// <summary>
    ///     The connections currently established through this switch,
    ///     each with the moment it was established. A connection
    ///     joins when its stack reports establishment and leaves
    ///     when its teardown completes.
    /// </summary>
    IReadOnlyDictionary<IRemotePeerConnection, DateTimeOffset> LiveConnections { get; }
}
