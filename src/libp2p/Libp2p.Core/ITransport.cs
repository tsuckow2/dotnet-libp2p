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
    /// <summary>
    ///     The multiaddr protocol this transport serves as the
    ///     outermost layer of an address: "tcp", "quic-v1",
    ///     "p2p-circuit". A declaration, not the protocol
    ///     <see cref="IProtocol.Id"/> — "ip-tcp" and "tcp" live in
    ///     different namespaces; this is the address component's own
    ///     name. The switch matches an address to the transport
    ///     declaring its outermost transport component.
    /// </summary>
    string AddressProtocol { get; }

    Task<IRemoteComm> DialAsync(Multiaddress address);

    Task ListenAsync(Multiaddress localAddress, Action<IChannel, Multiaddress> onConnection, CancellationToken token);
}
