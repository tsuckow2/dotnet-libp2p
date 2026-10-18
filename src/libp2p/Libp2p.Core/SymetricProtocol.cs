// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     Base for channel-tier protocols whose dial and listen paths are the
///     same negotiation (e.g. Plaintext, Yamux, selector protocols).
/// </summary>
public abstract class SymmetricProtocol
{
    public Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        return ConnectAsync(channel, channelFactory, connection, false);
    }

    public Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        return ConnectAsync(channel, channelFactory, connection, true);
    }

    protected abstract Task ConnectAsync(IChannel channel, IChannelFactory? channelFactory,
        IRemotePeerConnection connection, bool isListener);
}

/// <summary>
///     Base for managed-tier protocols whose dial and listen paths are
///     the same connect step. <typeparamref name="TOut"/> is the typed
///     comm the protocol hands back to its dialer. Negotiation is not
///     the protocol's business: the switch's multistream has already
///     selected this protocol on the stream before ConnectAsync runs,
///     on both sides — a second negotiation here would put bytes on
///     the wire that foreign peers (kubo) rightly reject.
/// </summary>
public abstract class SymmetricProtocol<TOut> : IProtocol<TOut>
{
    public abstract string Id { get; }

    public Task<IManagedProtocolComm<TOut>> DialAsync(IManagedRemoteComm comm)
    {
        return ConnectAsync(comm, false);
    }

    public async Task ListenAsync(IManagedRemoteComm comm)
    {
        await ConnectAsync(comm, true);
    }

    protected abstract Task<IManagedProtocolComm<TOut>> ConnectAsync(IManagedRemoteComm comm, bool isListener);
}
