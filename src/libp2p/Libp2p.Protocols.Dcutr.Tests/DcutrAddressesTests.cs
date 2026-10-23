// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Dcutr.Tests;

[TestFixture]
public class DcutrAddressesTests
{
    private static readonly string RelayId = new Identity().PeerId.ToString();

    private static Multiaddress Addr(string form) => Multiaddress.Decode(form);

    [Test]
    public void OwnAddresses_AreTheEarnedSet_DirectFormsOnly_QuicFirst()
    {
        var local = new LocalPeer(new Identity());
        string peerId = local.Identity.PeerId.ToString();
        // The bound QUIC listener (the transport files its bound
        // form, /p2p and all), its unbound template, the relayed
        // form we also listen on, and a TCP listener.
        local.GetOrAddAddress(Addr($"/ip4/198.51.100.9/udp/4001/quic-v1/p2p/{peerId}"));
        local.GetOrAddAddress(Addr("/ip4/0.0.0.0/udp/0/quic-v1"));
        local.GetOrAddAddress(Addr($"/ip4/203.0.113.1/tcp/9000/p2p/{RelayId}/p2p-circuit/p2p/{peerId}"));
        local.GetOrAddAddress(Addr("/ip4/198.51.100.9/tcp/4001"));

        IReadOnlyList<Multiaddress> own = DcutrAddresses.OwnAddresses(local);

        Assert.That(own.Select(a => a.ToString()), Is.EqualTo(new[]
        {
            "/ip4/198.51.100.9/udp/4001/quic-v1",
            "/ip4/198.51.100.9/tcp/4001",
        }));
    }

    [Test]
    public void OwnAddresses_ObservedForms_MustHaveEarnedAdvertisement()
    {
        var local = new LocalPeer(new Identity());
        local.GetOrAddAddress(Addr("/ip4/192.0.2.5/udp/4001/quic-v1"));
        // An observed candidate only one peer has reported: in the
        // book, but not yet part of the advertised set — and a
        // CONNECT must not offer what we would not advertise.
        local.GetOrAddAddress(Addr("/ip4/198.51.100.20/udp/9999/quic-v1"))
            .RecordVouch("reporter-1", AddressSource.Observed);

        IReadOnlyList<Multiaddress> own = DcutrAddresses.OwnAddresses(local);

        Assert.That(own.Select(a => a.ToString()),
            Is.EqualTo(new[] { "/ip4/192.0.2.5/udp/4001/quic-v1" }));
    }

    [Test]
    public void KnownDirectAddresses_RequireEvidence_AndSkipTheRelay()
    {
        var peer = new FakePeer(new Identity());
        string peerId = peer.Identity.PeerId.ToString();

        // No evidence at all: a bare referral is not dialed.
        peer.GetOrAddAddress(Addr("/ip4/203.0.113.8/udp/4001/quic-v1"))
            .RecordSource(AddressSource.Referral);
        // The peer's own Identify claim, QUIC and TCP.
        peer.GetOrAddAddress(Addr("/ip4/203.0.113.8/udp/4002/quic-v1"))
            .RecordVouch(peerId, AddressSource.SelfClaimed);
        peer.GetOrAddAddress(Addr("/ip4/203.0.113.8/tcp/4002"))
            .RecordVouch(peerId, AddressSource.SelfClaimed);
        // The circuit address is proven (we are talking over it) but
        // it is not a *direct* target.
        peer.GetOrAddAddress(Addr($"/ip4/203.0.113.1/tcp/9000/p2p/{RelayId}/p2p-circuit/p2p/{peerId}"))
            .RecordSuccessfulDial();

        IReadOnlyList<Multiaddress> known = DcutrAddresses.KnownDirectAddresses(peer);

        Assert.That(known.Select(a => a.ToString()), Is.EqualTo(new[]
        {
            "/ip4/203.0.113.8/udp/4002/quic-v1",
            "/ip4/203.0.113.8/tcp/4002",
        }));
    }

    [Test]
    public void ReceivedAddresses_DropThirdParties_CircuitForms_AndTemplates()
    {
        var sender = new PeerId(new Identity().PublicKey);
        string senderId = sender.ToString();
        var stranger = new PeerId(new Identity().PublicKey);

        Multiaddress[] received =
        [
            Addr("/ip4/203.0.113.9/udp/4001/quic-v1"),
            Addr($"/ip4/203.0.113.9/tcp/4001/p2p/{senderId}"),
            Addr($"/ip4/203.0.113.9/tcp/4002/p2p/{stranger}"),
            Addr($"/ip4/203.0.113.1/tcp/9000/p2p/{RelayId}/p2p-circuit/p2p/{senderId}"),
            Addr("/ip4/0.0.0.0/udp/4001/quic-v1"),
            Addr("/ip4/203.0.113.9/udp/0/quic-v1"),
        ];

        IReadOnlyList<Multiaddress> accepted = DcutrAddresses.ReceivedAddresses(received, sender);

        Assert.That(accepted.Select(a => a.ToString()), Is.EqualTo(new[]
        {
            "/ip4/203.0.113.9/udp/4001/quic-v1",
            "/ip4/203.0.113.9/tcp/4001",
        }));
    }
}
