// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface IRemoteConnnection
{
    Multiaddress Address { get; }
    Task DisconnectAsync();
}

public class GenericRemoteConnection(Multiaddress address, Func<Task> disconnect) : IRemoteConnnection
{
    public Multiaddress Address { get => address; }
    public Task DisconnectAsync()
    {
        return disconnect();
    }
}

public interface IManagedPeer
{

    IRemotePeer RemotePeer { get; }

    IChannelMultiplexer? Mux { get; set; }

    Task<IManagedProtocolComm<Out>> DialAsync<TProtocol, Out>(ProtocolRef<TProtocol, Out> r, CancellationToken token = default) where TProtocol : IProtocol<Out>;
}


public class Foo(IServiceProvider serviceProvider) : IManagedPeer
{
    public IRemotePeer RemotePeer { get => throw new NotImplementedException(); }
    public IChannelMultiplexer? Mux { get; set; }
    public Task<IManagedProtocolComm<Out>> DialAsync<TProtocol, Out>(ProtocolRef<TProtocol, Out> r, CancellationToken token = default) where TProtocol : IProtocol<Out>
    {
        var protocol = serviceProvider.GetService<TProtocol>();
        throw new NotImplementedException();
    }
}