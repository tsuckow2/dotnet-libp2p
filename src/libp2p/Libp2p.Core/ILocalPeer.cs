// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The local peer as information: an identity and an address book.
///     Listening and dialing live on the switch
///     (<see cref="IManagedPeerSwitch"/>), not on the peer.
/// </summary>
public interface ILocalPeer : IPeer
{
}
