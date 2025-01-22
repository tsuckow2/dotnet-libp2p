// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using MorseCode.ITask;

namespace Nethermind.Libp2p.Core;

public abstract class SymmetricProtocol<Out> : IProtocol<Out>
{
    public abstract string Id { get; }

    public async ITask<IManagedProtocolComm<Out>> DialAsync(IManagedRemoteComm comm)

    {
        await NegotiateAsync(comm);
        return await ConnectAsync(comm, false);
    }

    public async Task ListenAsync(IManagedRemoteComm channel)
    {
        await ConnectAsync(channel, true);
    }


    protected abstract Task NegotiateAsync(IManagedRemoteComm comm);
    protected abstract Task<IManagedProtocolComm<Out>> ConnectAsync(IManagedRemoteComm comm, bool isListener);
}
