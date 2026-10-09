// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The comm object a protocol hands back to its dialer: the protocol's
///     own typed surface (<typeparamref name="TComm"/>) plus a link to the
///     comm it was negotiated over.
/// </summary>
public interface IProtocolComm<out TComm>
{
    TComm comm { get; }
    IRemoteComm Parent { get; }
}

/// <summary>
///     A protocol comm whose parent is managed by the switch, so the dialer
///     can keep dialing further protocols over the same connection.
/// </summary>
public interface IManagedProtocolComm<out TComm> : IProtocolComm<TComm>
{
    IRemoteComm IProtocolComm<TComm>.Parent => Parent;
    new IManagedRemoteComm Parent { get; }
}

public class ManagedProtocolComm<TComm>(TComm _comm, IManagedRemoteComm _parent) : IManagedProtocolComm<TComm>
{
    public TComm comm => _comm;
    public IManagedRemoteComm Parent => _parent;
}

/// <summary>
///     A typed handle to a protocol. Protocols expose a static
///     <see cref="ProtocolRef{TProtocol, TOut}"/> so dialing is type-safe:
///     no string ids and no casting the result back.
/// </summary>
public interface IProtocolRef<out TProtocol, out TOut> where TProtocol : IProtocol<TOut>
{
}

public class ProtocolRef<TProtocol, TOut> : IProtocolRef<TProtocol, TOut> where TProtocol : IProtocol<TOut>
{
}

/// <summary>
///     A protocol that operates on comms without switch management.
///     The channel-tier role protocols (muxers, security) live here:
///     their <typeparamref name="TOut"/> is what a successful negotiation
///     produces (a multiplexer, a secured comm, ...).
/// </summary>
public interface IAnonymousProtocol<out TOut>
{
    /// <summary>
    ///     Id used during connection establishment to negotiate the protocol.
    /// </summary>
    string Id { get; }

    /// <summary>
    ///     Actively dials a peer over an unmanaged comm.
    /// </summary>
    Task<IProtocolComm<TOut>> DialAsync(IRemoteComm channel);

    /// <summary>
    ///     Opens a channel to listen to a remote peer over an unmanaged comm.
    /// </summary>
    Task ListenAsync(IRemoteComm channel);
}

/// <summary>
///     A protocol in the managed tier: like <see cref="IAnonymousProtocol{TOut}"/>,
///     but the comm is managed by the switch, and dialing returns the typed
///     comm so the caller gets the protocol's own surface.
/// </summary>
public interface IProtocol<out TOut>
{
    /// <summary>
    ///     Id used during connection establishment to negotiate the protocol.
    /// </summary>
    string Id { get; }

    /// <summary>
    ///     Actively dials a peer over a managed comm.
    /// </summary>
    Task<IManagedProtocolComm<TOut>> DialAsync(IManagedRemoteComm channel);

    /// <summary>
    ///     Opens a channel to listen to a remote peer over a managed comm.
    /// </summary>
    Task ListenAsync(IManagedRemoteComm channel);
}

/// <summary>
///     Managed-tier role: a muxer protocol produces a channel multiplexer.
/// </summary>
public interface IMuxerProtocol<out TOut> : IAnonymousProtocol<TOut> where TOut : IChannelMultiplexer
{
}

/// <summary>
///     Managed-tier role: a security protocol produces a secured comm.
/// </summary>
public interface ISecurityProtocol<out TOut> : IAnonymousProtocol<TOut> where TOut : IRemoteComm
{
}
