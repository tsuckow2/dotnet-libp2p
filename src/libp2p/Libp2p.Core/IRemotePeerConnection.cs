// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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
///     A managed connection to a remote peer, as returned by
///     <see cref="IPeerFactory"/>: who is at the other end, the managed view
///     used to dial protocols over it, and its multiplexer once a muxer has
///     been negotiated.
/// </summary>
public interface IRemotePeerConnection
{
    IRemotePeer RemotePeer { get; }
    IManagedPeer Managed { get; }
    IChannelMultiplexer? Mux { get; }
    ITransportConnection TransportConnection { get; }
    Task DisconnectAsync();
}
