// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A remote peer as information, plus the connection-bound surface the
///     channel-tier consumers use once the switch has connected it:
///     dialing a protocol over the live connection and disconnecting.
///     Managed-tier dialing goes through the connection's
///     <see cref="IManagedPeer"/> instead.
/// </summary>
public interface IRemotePeer : IPeer
{
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol;
    Task DisconnectAsync();
}
