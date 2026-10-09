// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface ILocalPeer : IPeer
{
    /// <summary>
    ///     Dial an address through the switch this peer is attached to and
    ///     return the remote peer at its end. The full connection object is
    ///     available from <see cref="IPeerFactory"/>; this surface is what
    ///     the channel-tier consumers (e.g. the pubsub router) were written
    ///     against.
    /// </summary>
    Task<IRemotePeer> DialAsync(Multiaddress addr, CancellationToken token = default);

    /// <summary>
    ///     Listen on an address through the attached switch.
    /// </summary>
    Task<IListener> ListenAsync(Multiaddress addr, CancellationToken token = default);
}
