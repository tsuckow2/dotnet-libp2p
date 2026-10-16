// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A remote peer as information: an identity and an address book.
///     Acting on a live connection — dialing protocols over it, closing
///     it — goes through the connection object
///     (<see cref="IRemotePeerConnection"/>), not the peer.
/// </summary>
public interface IRemotePeer : IPeer
{
}
