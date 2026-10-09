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
    /// <param name="context">Holds information about local and remote peers</param>
    Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context);

    /// <summary>
    ///     Opens a channel to listen to a remote peer.
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context);
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
