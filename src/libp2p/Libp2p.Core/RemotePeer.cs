
using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

internal class RemotePeer : IRemotePeer
{
    private readonly PeerFactory _factory;

    public RemotePeer(PeerFactory factory, Identity identity)
    {
        _factory = factory;
        Identity = identity;
    }

    public Identity Identity { get; }
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; } = new();
    public PeerAddressAccounting GetOrAddAddress(Multiaddress address)
    {
        return Addresses.GetOrAdd(address, (_key) => new PeerAddressAccounting());
    }

    public async Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
    {
        return _factory.DialAsync<TProtocol>(peerContext, token);
    }
}