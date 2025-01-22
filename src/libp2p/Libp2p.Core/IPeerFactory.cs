// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface IPeerFactory
{
    /// <summary>
    /// Connect to a peer using a specific multiaddr and enforce the identity to match the provided remote peer
    /// </summary>
    /// <param name="peer">Remote peer to validate the connection to</param>
    /// <param name="addr">Address to dial</param>
    /// <param name="token">Cancellation token</param>
    /// <returns></returns>
    Task<IRemotePeerConnection> DialAsync(IRemotePeer peer, Multiaddress addr, CancellationToken token);

    /// <summary>
    /// Connect to a peer using any available multiaddr or existing connection
    /// </summary>
    /// <param name="peer">Remote peer to connect to</param>
    /// <param name="token">Cancellation token</param>
    /// <returns></returns>
    Task<IRemotePeerConnection> DialAsync(IRemotePeer peer, CancellationToken token);

    /// <summary>
    /// Connect to a specific multiaddr and don't care what identity is at the other end.
    /// </summary>
    /// <param name="addr">Address to dial</param>
    /// <param name="token">Cancellation token</param>
    /// <returns></returns>
    Task<IRemotePeerConnection> DialAsync(Multiaddress addr, CancellationToken token);
}
