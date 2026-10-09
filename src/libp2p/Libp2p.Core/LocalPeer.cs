// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The local peer: its addresses are the listen set for the switch.
///     The switch attaches itself on construction so the peer's
///     connection-bound surface can delegate to it.
/// </summary>
public class LocalPeer(Identity identity) : ILocalPeer
{
    private IManagedPeerSwitch? _switch;
    private Multiaddress? _address;

    public Identity Identity { get; set; } = identity;
    public ConcurrentDictionary<Multiaddress, PeerAddressAccounting> Addresses { get; } = new();

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

    internal void AttachSwitch(IManagedPeerSwitch peerSwitch)
    {
        _switch = peerSwitch;
    }

    public async Task<IRemotePeer> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        IRemotePeerConnection connection = await Switch.DialAsync(addr, token);
        return connection.RemotePeer;
    }

    public Task<IListener> ListenAsync(Multiaddress addr, CancellationToken token = default)
    {
        GetOrAddAddress(addr);
        return Switch.ListenAsync(this, token);
    }

    private IManagedPeerSwitch Switch => _switch
        ?? throw new InvalidOperationException("This local peer is not attached to a switch. Build it through a peer factory builder (AddLibp2p) so the switch can attach.");
}
