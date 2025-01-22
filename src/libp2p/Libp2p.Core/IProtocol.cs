// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using MorseCode.ITask;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

// public interface IProtocolComm
// {
//     IRemoteComm Parent { get; }
// }


// public interface IProtocolPeerChannel<out TChannel> where TChannel : IProtocolComm
// {

//     IRemotePeerChannel Channel { get; }
// }

public interface IProtocolComm<out TComm> //where TComm : IProtocolComm
{
    TComm comm { get; }
    IRemoteComm Parent { get; }
}


public interface IManagedProtocolComm<out TComm> : IProtocolComm<TComm>// where TComm : IProtocolComm
{
    IRemoteComm IProtocolComm<TComm>.Parent { get => Parent; }
    new IManagedRemoteComm Parent { get; }
}

public class ManagedProtocolComm<TComm>(TComm _comm, IManagedRemoteComm _parent) : IManagedProtocolComm<TComm> //where TComm : IProtocolComm
{

    public TComm comm { get => _comm; }
    public IManagedRemoteComm Parent { get => _parent; }
}

// public class ProtocolPeerChannel<TChannel>(IRemotePeerChannel channel) : IProtocolPeerChannel<TChannel> where TChannel : IProtocolChannel
// {
//     public IRemotePeerChannel Channel { get => channel; }
// }

// public class GenericProtocolChannel : IProtocolChannel
// {
//     public IRemotePeerChannel Channel { get; }

//     public GenericProtocolChannel(IRemotePeerChannel channel)
//     {
//         Channel = channel;
//     }
// }

public interface IProtocolRef<out TProtocol, out Out> where TProtocol : IProtocol<Out>
{
}

public class ProtocolRef<TProtocol, Out> : IProtocolRef<TProtocol, Out> where TProtocol : IProtocol<Out>
{
    public ProtocolRef()
    {
    }
}

public interface IAnonymousProtocol<out Out> //where Out : IProtocolComm
{
    /// <summary>
    ///     Id used to during connection establishment, exchanging information about protocol versions and so on
    /// </summary>
    string Id
    { get; }

    /// <summary>
    ///     Actively dials a peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    ITask<IProtocolComm<Out>> DialAsync(IRemoteComm channel);

    /// <summary>
    ///     Opens a channel to listen to a remote peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    Task ListenAsync(IRemoteComm channel);
}

public interface IProtocol<out Out> //where Out : IProtocolChannel
{
    /// <summary>
    ///     Id used to during connection establishedment, exchanging information about protocol versions and so on
    /// </summary>
    string Id
    { get; }

    /// <summary>
    ///     Actively dials a peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    ITask<IManagedProtocolComm<Out>> DialAsync(IManagedRemoteComm channel);

    /// <summary>
    ///     Opens a channel to listen to a remote peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    Task ListenAsync(IManagedRemoteComm channel);
}

public interface ITransport
{
    Task<IRemoteComm> DialAsync(Multiaddress address);

    Task ListenAsync(Multiaddress localAddress, Action<IChannel, Multiaddress> onConnection, CancellationToken token);
}

public interface IMuxerProtocol<out Out> : IAnonymousProtocol<Out> where Out : IChannelMultiplexer
{
    // Task<IChannelMultiplexer> DialAsync(IRemoteComm channel);

    // //Handle an incoming connection
    // Task<IChannelMultiplexer> ListenAsync(IRemoteComm channel);
}

public interface ISecurityProtocol<out Out> : IAnonymousProtocol<Out> where Out : IRemoteComm
{
    // Task<IChannel> DialAsync(IRemotePeerConnection connection, IChannel channel);
}

public interface IIdentityProtocol { }