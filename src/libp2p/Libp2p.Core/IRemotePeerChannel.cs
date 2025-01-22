// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public interface IRemoteComm
{
    bool Incoming { get; }
    IChannel Channel { get; }
    IRemoteConnnection Connection { get; }
}

public interface IManagedRemoteComm : IRemoteComm
{
    IManagedPeer Managed { get; }
}

public class GenericRemoteComm(bool incoming, IChannel channel, IRemoteConnnection connection) : IRemoteComm
{
    public bool Incoming { get => incoming; }
    public IChannel Channel { get => channel; }
    public IRemoteConnnection Connection { get => connection; }
}

public class GenericManagedRemoteComm(bool incoming, IChannel channel, IRemoteConnnection connection, IManagedPeer _peer) : IManagedRemoteComm
{
    public bool Incoming { get => incoming; }
    public IChannel Channel { get => channel; }
    public IRemoteConnnection Connection { get => connection; }
    public IManagedPeer Managed { get => _peer; }
}

public class GenericWrappedManagedRemoteComm(IRemoteComm _remoteComm, IManagedPeer _peer) : IManagedRemoteComm
{
    public bool Incoming { get => _remoteComm.Incoming; }
    public IChannel Channel { get => _remoteComm.Channel; }
    public IRemoteConnnection Connection { get => _remoteComm.Connection; }
    public IManagedPeer Managed { get => _peer; }
}
