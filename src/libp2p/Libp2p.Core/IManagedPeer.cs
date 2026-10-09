// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The managed view of a connected remote peer: dial managed-tier
///     protocols over the live connection by their typed
///     <see cref="ProtocolRef{TProtocol, TOut}"/>.
/// </summary>
public interface IManagedPeer
{
    IRemotePeer RemotePeer { get; }

    IChannelMultiplexer? Mux { get; set; }

    Task<IManagedProtocolComm<TOut>> DialAsync<TProtocol, TOut>(ProtocolRef<TProtocol, TOut> protocolRef, CancellationToken token = default)
        where TProtocol : IProtocol<TOut>;
}
