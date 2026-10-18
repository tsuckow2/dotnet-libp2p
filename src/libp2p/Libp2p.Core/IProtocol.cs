// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A protocol in the channel tier: it speaks over a single channel and
///     uses the channel factory to spawn channels for the protocols above it.
///     Application protocols that need a typed, managed surface implement
///     <see cref="IProtocol{TOut}"/> instead; the two tiers are bridged by
///     the Generic* comm adapters.
/// </summary>
public interface IProtocol
{
    /// <summary>
    ///     Id used during connection establishment to negotiate the protocol.
    /// </summary>
    string Id { get; }

    /// <summary>
    ///     Actively dials a peer.
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="connection">The connection being established: it carries both peers and the endpoints, so a protocol reads everything it needs about the ends from the connection itself</param>
    Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IRemotePeerConnection connection);

    /// <summary>
    ///     Opens a channel to listen to a remote peer.
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="connection">The connection being established (for a listening transport, a shell carrying the local peer, one per listener; transports create the per-connection objects through the channel factory)</param>
    Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IRemotePeerConnection connection);
}

/// <summary>
///     A selector protocol (multistream) that can be told to propose one
///     specific protocol instead of trying each of its sub-protocols in
///     turn. When a muxer opens a stream for a specific sub-dial request,
///     the request cannot ride the connection — streams over one
///     connection are concurrent — so the channel factory dials through
///     a per-stream instance of the selector carrying the request here.
/// </summary>
public interface ISpecificProtocolSelector : IProtocol
{
    IChannelRequest? SpecificRequest { get; set; }
}

/// <summary>
///     Marker for channel-tier protocols that secure a connection
///     (e.g. Noise, Plaintext). Categorization is by role so the registry
///     and the switch can find the protocol without a stack definition.
/// </summary>
public interface ISecurityProtocol : IProtocol
{
}

/// <summary>
///     Marker for channel-tier protocols that multiplex a connection
///     into multiple channels (e.g. Yamux).
/// </summary>
public interface IMuxerProtocol : IProtocol
{
}

/// <summary>
///     Marker for the channel-tier protocol that exchanges peer identity
///     and observed addresses (Identify).
/// </summary>
public interface IIdentityProtocol : IProtocol
{
}
