// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelFactory
{
    IEnumerable<IProtocol> SubProtocols { get; }
    IChannel SubDial(IRemotePeerConnection connection, IChannelRequest? request = null);

    /// <summary>
    ///     Opens a stream channel for a queued sub-dial request: dials
    ///     the layer above (the selector), which proposes just the
    ///     requested protocol. Used by muxers; the muxer completes the
    ///     request when the returned channel closes.
    /// </summary>
    IChannel SubDialForRequest(IRemotePeerConnection connection, IChannelRequest request);

    /// <summary>
    ///     Creates the connection for a newly accepted transport
    ///     connection. Only a listen chain's root factory can do this:
    ///     the switch installs the creation logic there, wired to the
    ///     listener the transport is accepting for.
    /// </summary>
    IRemotePeerConnection AcceptConnection();

    IChannel SubListen(IRemotePeerConnection connection, IChannelRequest? request = null);

    Task SubDialAndBind(IChannel parentChannel, IRemotePeerConnection connection, IChannelRequest? request = null);

    Task SubListenAndBind(IChannel parentChannel, IRemotePeerConnection connection, IChannelRequest? request = null);



    IChannel SubDial(IRemotePeerConnection connection, IProtocol protocol)
    {
        return SubDial(connection, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubListen(IRemotePeerConnection connection, IProtocol protocol)
    {
        return SubListen(connection, new ChannelRequest { SubProtocol = protocol });
    }

    Task SubDialAndBind(IChannel parentChannel, IRemotePeerConnection connection, IProtocol protocol)
    {
        return SubDialAndBind(parentChannel, connection, new ChannelRequest { SubProtocol = protocol });
    }

    Task SubListenAndBind(IChannel parentChannel, IRemotePeerConnection connection, IProtocol protocol)
    {
        return SubListenAndBind(parentChannel, connection, new ChannelRequest { SubProtocol = protocol });
    }
}
