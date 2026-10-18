// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
public class MultiaddressExtensionsTests
{
    [Test]
    public void TerminalP2P_OfARoutedAddress_IsTheDestination()
    {
        var relay = new Identity();
        var destination = new Identity();
        var address = Multiaddress.Decode(
            $"/ip4/127.0.0.1/tcp/4001/p2p/{relay.PeerId}/p2p-circuit/p2p/{destination.PeerId}");

        Assert.That(address.TerminalP2P()?.ToString(), Is.EqualTo(destination.PeerId.ToString()));
    }

    [Test]
    public void TerminalP2P_OfADirectAddress_IsItsOnlyP2P()
    {
        var peer = new Identity();
        var address = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{peer.PeerId}");

        Assert.That(address.TerminalP2P()?.ToString(), Is.EqualTo(peer.PeerId.ToString()));
    }

    [Test]
    public void TerminalP2P_OfAnAddressWithoutP2P_IsNull()
    {
        var address = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001");

        Assert.That(address.TerminalP2P(), Is.Null);
    }
}
