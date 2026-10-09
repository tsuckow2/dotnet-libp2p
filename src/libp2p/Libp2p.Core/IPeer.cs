// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A peer as information: an identity and the addresses it is known at,
///     each with its own accounting. A peer is not a connection — connections
///     to a peer are separate, disposable objects
///     (<see cref="IRemotePeerConnection"/>).
/// </summary>
public interface IPeer
{
    Identity Identity { get; set; }

    /// <summary>
    ///     The peer's primary address: the first one recorded in
    ///     <see cref="Addresses"/>, unless set explicitly. Kept for the
    ///     channel-tier consumers (Noise, the selector protocol, the pubsub
    ///     router) that reason about one address per peer.
    /// </summary>
    Multiaddress Address { get; set; }

    ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; }
    PeerAddressAccounting GetOrAddAddress(Multiaddress address);
}
