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
    Identity Identity { get; }

    ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; }
    PeerAddressAccounting GetOrAddAddress(Multiaddress address);
}
