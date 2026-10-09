// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     One conversation with a remote peer at the transport level: a channel
///     plus the connection it arrived on. Protocols in the anonymous and
///     channel tiers speak over comms without knowing about the switch.
/// </summary>
public interface IRemoteComm
{
    bool Incoming { get; }
    IChannel Channel { get; }
    ITransportConnection Connection { get; }
}

/// <summary>
///     A comm whose connection is managed by the switch: protocols in the
///     managed tier get the managed peer alongside the channel, so further
///     protocols can be dialed over the same connection.
/// </summary>
public interface IManagedRemoteComm : IRemoteComm
{
    IManagedPeer Managed { get; }
}

public class GenericRemoteComm(bool incoming, IChannel channel, ITransportConnection connection) : IRemoteComm
{
    public bool Incoming => incoming;
    public IChannel Channel => channel;
    public ITransportConnection Connection => connection;
}

public class GenericManagedRemoteComm(bool incoming, IChannel channel, ITransportConnection connection, IManagedPeer managed) : IManagedRemoteComm
{
    public bool Incoming => incoming;
    public IChannel Channel => channel;
    public ITransportConnection Connection => connection;
    public IManagedPeer Managed => managed;
}

/// <summary>
///     Adapts an unmanaged comm to a managed one, e.g. when a connection
///     becomes switch-managed after negotiation.
/// </summary>
public class GenericWrappedManagedRemoteComm(IRemoteComm remoteComm, IManagedPeer managed) : IManagedRemoteComm
{
    public bool Incoming => remoteComm.Incoming;
    public IChannel Channel => remoteComm.Channel;
    public ITransportConnection Connection => remoteComm.Connection;
    public IManagedPeer Managed => managed;
}
