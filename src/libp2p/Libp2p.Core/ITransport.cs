// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A transport: dials and listens at the address level, below every
///     protocol layer. Implementations adapt their native connection type
///     to <see cref="IRemoteComm"/> (see GenericRemoteConnection /
///     GenericRemoteComm) so the switch never sees a socket.
/// </summary>
public interface ITransport
{
    Task<IRemoteComm> DialAsync(Multiaddress address);

    Task ListenAsync(Multiaddress localAddress, Action<IChannel, Multiaddress> onConnection, CancellationToken token);
}
