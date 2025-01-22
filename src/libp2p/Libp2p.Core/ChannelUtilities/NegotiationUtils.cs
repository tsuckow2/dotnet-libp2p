using System;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Core;

public static class NegotiationUtils
{
    public static async Task NegotiateProtocolAsync(IRemoteComm channel, string protocol)
    {
        // Send supported protocols to the remote peer
        await channel.Channel.WriteLineAsync(protocol);
        var response = await channel.Channel.ReadLineAsync();

        if (response != protocol)
        {
            if (response == "na")
            {
                throw new FailedToNegotiateException($"Protocol `{protocol}` was not available on the remote peer");
            }
            else
            {
                throw new InvalidOperationException("Negotiation failed: Unexpected Response");
            }

        }
    }
}