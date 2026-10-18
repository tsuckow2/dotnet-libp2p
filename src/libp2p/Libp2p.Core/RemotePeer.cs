// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

internal class RemotePeer : IRemotePeer
{
    private Multiaddress? _address;
    private Identity _identity;

    public RemotePeer(Identity identity)
    {
        _identity = identity;
        Addresses = new ConcurrentDictionary<Multiaddress, PeerAddressAccounting>();
    }

    /// <summary>
    ///     Raised when the peer's identity is replaced by a different
    ///     one — Identify naming a peer that started anonymous. The
    ///     switch canonicalizes its peer registry on this signal.
    /// </summary>
    internal event Action<RemotePeer, Identity>? IdentityEstablished;

    public Identity Identity
    {
        get => _identity;
        set
        {
            Identity previous = _identity;
            _identity = value;
            if (!previous.PeerId.Bytes.AsSpan().SequenceEqual(value.PeerId.Bytes))
            {
                IdentityEstablished?.Invoke(this, previous);
            }
        }
    }
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
    ///     Folds another peer object's address book into this one when
    ///     the two turn out to be the same peer (the other started
    ///     anonymous and was named by Identify after this one was
    ///     already canonical). Addresses only the other knew are
    ///     adopted with their accounting; for shared addresses the
    ///     worse failure count wins.
    /// </summary>
    internal void AbsorbAddressesFrom(RemotePeer other)
    {
        foreach ((Multiaddress address, PeerAddressAccounting accounting) in other.Addresses)
        {
            Addresses.AddOrUpdate(address, accounting, (_, existing) =>
            {
                existing.Absorb(accounting);
                return existing;
            });
        }
    }
}
