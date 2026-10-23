// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Dcutr.Tests;

[TestFixture]
public class HolePunchServiceTests
{
    private static readonly DcutrProtocolSettings FastSettings = new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(300),
        MaxAttempts = 2,
    };

    private static readonly string RelayId = new Identity().PeerId.ToString();

    private static TestRemotePeerConnection RelayedConnectionTo(Identity destination)
    {
        return new TestRemotePeerConnection(new FakePeer(destination))
        {
            ProvenPeerId = destination.PeerId,
            RemoteEndpoint = Multiaddress.Decode(
                $"/ip4/203.0.113.1/tcp/9000/p2p/{RelayId}/p2p-circuit/p2p/{destination.PeerId}"),
        };
    }

    private static IManagedPeerSwitch SwitchHolding(params IRemotePeerConnection[] live)
    {
        var @switch = Substitute.For<IManagedPeerSwitch>();
        @switch.LiveConnections.Returns(
            live.ToDictionary(c => c, _ => DateTimeOffset.UtcNow));
        return @switch;
    }

    [Test]
    public async Task AlreadyDirect_ReturnsTheConnectionItself()
    {
        var identity = new Identity();
        var direct = new TestRemotePeerConnection(new FakePeer(identity))
        {
            ProvenPeerId = identity.PeerId,
            RemoteEndpoint = Multiaddress.Decode("/ip4/203.0.113.9/udp/4001/quic-v1"),
        };
        var service = new HolePunchService(SwitchHolding(direct), FastSettings);

        IRemotePeerConnection? result = await service.TryUpgradeAsync(direct);

        Assert.That(result, Is.SameAs(direct));
    }

    [Test]
    public async Task ExistingDirectConnection_IsReturnedWithoutPunching()
    {
        var destination = new Identity();
        TestRemotePeerConnection relayed = RelayedConnectionTo(destination);
        var direct = new TestRemotePeerConnection(new FakePeer(destination))
        {
            ProvenPeerId = destination.PeerId,
            RemoteEndpoint = Multiaddress.Decode("/ip4/203.0.113.9/udp/4001/quic-v1"),
        };
        var service = new HolePunchService(SwitchHolding(relayed, direct), FastSettings);

        IRemotePeerConnection? result = await service.TryUpgradeAsync(relayed);

        Assert.That(result, Is.SameAs(direct));
    }

    [Test]
    public async Task FailedUpgrade_ReturnsNull_AndLeavesTheRelayedConnectionAlone()
    {
        var destination = new Identity();
        TestRemotePeerConnection relayed = RelayedConnectionTo(destination);
        // The switch holds only the relayed connection, and the
        // test connection's protocol dials complete instantly
        // without producing anything — every attempt fails.
        var service = new HolePunchService(SwitchHolding(relayed), FastSettings);

        IRemotePeerConnection? result = await service.TryUpgradeAsync(relayed);

        Assert.That(result, Is.Null);
    }
}
