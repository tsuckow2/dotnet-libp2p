// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Bitswap;

/// <summary>
///     Bitswap 1.1.0 (https://github.com/ipfs/specs/blob/main/BITSWAP.md),
///     as a managed-tier protocol: negotiate, then exchange protobuf
///     Messages of wantlists and block payloads. The protocol only speaks
///     wire format; the session it returns does the exchange.
/// </summary>
public class BitSwap11Protocol(IBlockStore? blockStore = null) : SymmetricProtocol<BitswapSession>
{
    public static ProtocolRef<BitSwap11Protocol, BitswapSession> Ref => new();

    public override string Id => "/ipfs/bitswap/1.1.0";

    protected override async Task NegotiateAsync(IManagedRemoteComm comm)
    {
        await NegotiationUtils.NegotiateProtocolAsync(comm, Id);
    }

    protected override Task<IManagedProtocolComm<BitswapSession>> ConnectAsync(IManagedRemoteComm comm, bool isListener)
    {
        var session = new BitswapSession(comm.Channel, blockStore ?? new InMemoryBlockStore());
        return Task.FromResult<IManagedProtocolComm<BitswapSession>>(new ManagedProtocolComm<BitswapSession>(session, comm));
    }
}
