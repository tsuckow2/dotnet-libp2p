// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A transport-level connection: an address and a way to close it.
///     Transports hand these to the switch wrapped in an
///     <see cref="IRemoteComm"/>; nothing above the transport layer should
///     need one directly.
/// </summary>
public interface ITransportConnection
{
    Multiaddress Address { get; }
    Task DisconnectAsync();
}

public class GenericRemoteConnection(Multiaddress address, Func<Task> disconnect) : ITransportConnection
{
    public Multiaddress Address => address;

    public Task DisconnectAsync()
    {
        return disconnect();
    }
}

/// <summary>
///     A managed connection to a remote peer: who is at each end, the
///     managed view used to dial protocols over it, and its multiplexer
///     once a muxer has been negotiated. The connection is also what the
///     channel-tier stack passes from layer to layer while the
///     connection is being established — the layers read the peers and
///     endpoints from it, transports fill the endpoints in, and the
///     muxer reports establishment through it. There is no separate
///     per-connection context object.
/// </summary>
public interface IRemotePeerConnection
{
    ILocalPeer LocalPeer { get; }
    IRemotePeer RemotePeer { get; }
    IManagedPeer Managed { get; }
    IChannelMultiplexer? Mux { get; }
    ITransportConnection TransportConnection { get; }

    /// <summary>
    ///     The address this connection was reached at, as the transport
    ///     observed it. Filled in by the transport while the connection
    ///     is being established.
    /// </summary>
    Multiaddress? RemoteEndpoint { get; set; }

    /// <summary>
    ///     Our address on this connection, as the transport observed
    ///     it. Filled in by the transport while the connection is being
    ///     established.
    /// </summary>
    Multiaddress? LocalEndpoint { get; set; }

    /// <summary>
    ///     The peer id this connection is expected to reach, when the
    ///     dial named one: the terminal /p2p of the dialed address.
    ///     Set by the switch at formation on addressed dials; null for
    ///     anonymous dials and accepted connections, where no peer was
    ///     named in advance. The security layer — the one place a
    ///     remote key is proven — faults the handshake when the proven
    ///     identity is not this one, so a connection handed out for a
    ///     named peer is that peer, not whoever answered at its
    ///     address. (A stack with no security layer has no proof and
    ///     enforces nothing.)
    /// </summary>
    PeerId? ExpectedPeerId { get; set; }

    /// <summary>
    ///     The peer id the security layer proved during the handshake,
    ///     derived from the authenticated remote key. Set together with
    ///     the remote peer's identity, which the security layer
    ///     establishes from the same key; null until a security layer
    ///     has run. Identify cross-checks its message against this and
    ///     never renames a proven peer.
    /// </summary>
    PeerId? ProvenPeerId { get; set; }

    /// <summary>
    ///     Sub-dial requests for this connection: protocols and the
    ///     switch queue work here, and the connection's muxer consumes
    ///     it, opening a stream per request. Each connection owns its
    ///     queue.
    /// </summary>
    BlockingCollection<IChannelRequest> SubDialRequests { get; }

    /// <summary>
    ///     Stack signaling: the muxer calls this when the connection is
    ///     established, which lets the switch publish the connection
    ///     and start the identity protocols on it.
    /// </summary>
    void Connected();

    /// <summary>
    ///     Stack signaling for a listening transport: called on the
    ///     listener's shell connection when the transport is bound and
    ///     ready to accept, which completes the switch's listen.
    /// </summary>
    void ListenerReady();

    Task DisconnectAsync();

    /// <summary>
    ///     Dial a channel-tier protocol over this connection: the muxer
    ///     opens a stream for it, and the task completes when the
    ///     protocol's channel closes. Managed-tier protocols are dialed
    ///     through <see cref="Managed"/> instead.
    /// </summary>
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol;
}
