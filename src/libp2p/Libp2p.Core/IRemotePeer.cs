// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A remote peer as information. Dialing is not the peer's job: open a
///     connection through <see cref="IPeerFactory"/> and dial protocols over
///     the connection's <see cref="IManagedPeer"/>.
/// </summary>
public interface IRemotePeer : IPeer
{
}
