// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

internal class RemotePeer : IRemotePeer
{
    private Multiaddress? _address;
    private IRemotePeerConnection? _connection;

    public RemotePeer(Identity identity)
    {
        Identity = identity;
        Addresses = new ConcurrentDictionary<Multiaddress, PeerAddressAccounting>();
    }

    private RemotePeer(Identity identity, ConcurrentDictionary<Multiaddress, PeerAddressAccounting> addresses)
    {
        Identity = identity;
        Addresses = addresses;
    }

    public Identity Identity { get; set; }
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; }

    public Multiaddress Address
    {
        get => _address ?? Addresses.Keys.FirstOrDefault()!;
        set
        {
            _address = value;
            GetOrAddAddress(value);
        }
    }

    public PeerAddressAccounting GetOrAddAddress(Multiaddress address)
    {
        return Addresses.GetOrAdd(address, _ => new PeerAddressAccounting());
    }

    /// <summary>
    ///     A fork for a new peer context: same peer, same address book.
    /// </summary>
    internal RemotePeer Fork()
    {
        return new RemotePeer(Identity, Addresses);
    }

    internal void AttachConnection(IRemotePeerConnection connection)
    {
        _connection = connection;
    }

    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
    {
        // Channel-tier protocol dialing over a live connection lands with
        // the switch's upgrade sequence (plan Phase 3).
        throw new NotImplementedException("Protocol dialing over a connection is not wired yet.");
    }

    public Task DisconnectAsync()
    {
        return _connection?.DisconnectAsync() ?? Task.CompletedTask;
    }
}
