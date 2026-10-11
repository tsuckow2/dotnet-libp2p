// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     Bridges a managed-tier protocol into the channel-tier stack: the
///     adapter sits in the top channel factory under the protocol's id, so
///     multistream selects it like any other protocol, and it runs the
///     managed protocol over a managed comm built from the stream channel.
///
///     Managed protocols negotiate on dial (see SymmetricProtocol /
///     NegotiationUtils) and do not negotiate on listen, so on the listen
///     side the adapter first answers that negotiation — the protocol id
///     line the dialer sends after multistream selection — and only then
///     hands the channel to the protocol.
/// </summary>
internal class ManagedProtocolAdapter<TProtocol, TOut>(IServiceProvider serviceProvider, ConnectionRegistry connectionRegistry)
    : IProtocol where TProtocol : IProtocol<TOut>
{
    private TProtocol? _protocol;
    private readonly ConcurrentQueue<TaskCompletionSource<IManagedProtocolComm<TOut>>> _pendingDials = new();

    private TProtocol Protocol => _protocol ??= ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(serviceProvider);

    public string Id => Protocol.Id;

    /// <summary>
    ///     Registers a dial the switch is about to queue for this protocol
    ///     and returns the completion the dialer awaits. Dials complete in
    ///     queue order (the muxer consumes sub-dial requests sequentially).
    ///     The completion cannot travel on the context: multistream clears
    ///     SpecificProtocolRequest before binding the selected protocol.
    /// </summary>
    public TaskCompletionSource<IManagedProtocolComm<TOut>> BeginDial()
    {
        var completion = new TaskCompletionSource<IManagedProtocolComm<TOut>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDials.Enqueue(completion);
        return completion;
    }

    public async Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        _pendingDials.TryDequeue(out TaskCompletionSource<IManagedProtocolComm<TOut>>? completion);
        try
        {
            IRemotePeerConnection connection = GetConnection(context);
            IManagedRemoteComm comm = new GenericManagedRemoteComm(false, downChannel, connection.TransportConnection, connection.Managed);
            IManagedProtocolComm<TOut> result = await Protocol.DialAsync(comm);
            completion?.TrySetResult(result);
        }
        catch (Exception e)
        {
            completion?.TrySetException(e);
            throw;
        }

        // In the channel tier a protocol task owns its channel for the
        // task's whole lifetime (the factory closes the channel when the
        // task ends), while a managed protocol returns a comm and keeps
        // working in the background. Hold the channel open until it
        // closes so the session is not cut off from under the comm.
        await downChannel;
    }

    public async Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        IRemotePeerConnection connection = GetConnection(context);

        // Answer the managed tier's own negotiation (see class summary).
        string proposed = await downChannel.ReadLineAsync();
        if (proposed != Id)
        {
            await downChannel.WriteLineAsync("na");
            return;
        }
        await downChannel.WriteLineAsync(Id);

        IManagedRemoteComm comm = new GenericManagedRemoteComm(true, downChannel, connection.TransportConnection, connection.Managed);
        await Protocol.ListenAsync(comm);

        // Same lifetime bridge as the dial side: keep the channel open
        // for the session the comm started.
        await downChannel;
    }

    private IRemotePeerConnection GetConnection(IPeerContext context)
    {
        return connectionRegistry.Get(context)
            ?? throw new InvalidOperationException($"No connection registered for context {context.Id}; the switch creates connections when the muxer reports Connected.");
    }
}
