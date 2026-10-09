// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     Base for channel-tier protocols whose dial and listen paths are the
///     same negotiation (e.g. Plaintext, Yamux, selector protocols).
/// </summary>
public abstract class SymmetricProtocol
{
    public Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        return ConnectAsync(channel, channelFactory, context, false);
    }

    public Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        return ConnectAsync(channel, channelFactory, context, true);
    }

    protected abstract Task ConnectAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context, bool isListener);
}

/// <summary>
///     Base for managed-tier protocols whose dial and listen paths share a
///     negotiation step. <typeparamref name="TOut"/> is the typed comm the
///     protocol hands back to its dialer.
/// </summary>
public abstract class SymmetricProtocol<TOut> : IProtocol<TOut>
{
    public abstract string Id { get; }

    public async Task<IManagedProtocolComm<TOut>> DialAsync(IManagedRemoteComm comm)
    {
        await NegotiateAsync(comm);
        return await ConnectAsync(comm, false);
    }

    public async Task ListenAsync(IManagedRemoteComm comm)
    {
        await ConnectAsync(comm, true);
    }

    protected abstract Task NegotiateAsync(IManagedRemoteComm comm);
    protected abstract Task<IManagedProtocolComm<TOut>> ConnectAsync(IManagedRemoteComm comm, bool isListener);
}
