// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Core.Tests;

public class PeerAddressAccountingTests
{
    [Test]
    public void DialOutcomes_AreCountedAndTimestamped()
    {
        var accounting = new PeerAddressAccounting();
        Assert.That(accounting.LastSuccessAt, Is.Null);
        Assert.That(accounting.LastFailureAt, Is.Null);

        accounting.RecordFailedDial();
        accounting.RecordFailedDial();
        accounting.RecordSuccessfulDial();

        Assert.That(accounting.FailedDials, Is.EqualTo(2));
        Assert.That(accounting.SuccessfulDials, Is.EqualTo(1));
        Assert.That(accounting.LastSuccessAt, Is.Not.Null);
        Assert.That(accounting.LastFailureAt, Is.Not.Null);
        Assert.That(accounting.LastConfirmedAt, Is.Not.Null);
    }

    [Test]
    public void Vouches_CountDistinctPeersOnly()
    {
        var accounting = new PeerAddressAccounting();

        accounting.RecordVouch("peer-a", AddressSource.Referral);
        accounting.RecordVouch("peer-a", AddressSource.Referral);
        accounting.RecordVouch("peer-b", AddressSource.SelfClaimed);

        Assert.That(accounting.VouchCount, Is.EqualTo(2));
        Assert.That(accounting.Sources, Is.EqualTo(AddressSource.Referral | AddressSource.SelfClaimed));
    }

    [Test]
    public void Absorb_KeepsTheWorseFailuresTheLatestNewsAndEveryVoucher()
    {
        var left = new PeerAddressAccounting();
        left.RecordFailedDial();
        left.RecordVouch("peer-a", AddressSource.Referral);

        var right = new PeerAddressAccounting();
        right.RecordFailedDial();
        right.RecordFailedDial();
        right.RecordSuccessfulDial();
        right.RecordVouch("peer-b", AddressSource.Observed);

        left.Absorb(right);

        Assert.That(left.FailedDials, Is.EqualTo(2));
        Assert.That(left.SuccessfulDials, Is.EqualTo(1));
        Assert.That(left.VouchCount, Is.EqualTo(2));
        Assert.That(left.Sources, Is.EqualTo(
            AddressSource.Referral | AddressSource.Observed));
        Assert.That(left.LastSuccessAt, Is.Not.Null);
    }
}

public class AdvertisedAddressesTests
{
    private static LocalPeer PeerWith(params string[] addresses)
    {
        var peer = new LocalPeer(new Identity());
        foreach (string address in addresses)
        {
            peer.GetOrAddAddress(Multiaddress.Decode(address));
        }
        return peer;
    }

    [Test]
    public void WildcardTemplatesAreNotAdvertised_ConcreteOnesAre()
    {
        LocalPeer peer = PeerWith("/ip4/0.0.0.0/tcp/4001", "/ip4/192.168.1.10/tcp/4001");

        IReadOnlyList<Multiaddress> advertised = peer.AdvertisedAddresses();

        Assert.That(advertised.Select(a => a.ToString()),
            Is.EqualTo(new[] { "/ip4/192.168.1.10/tcp/4001" }));
    }

    [Test]
    public void ObservedCandidatesMustEarnAdvertisementByAgreement()
    {
        LocalPeer peer = PeerWith("/ip4/0.0.0.0/tcp/4001");
        Multiaddress observed = Multiaddress.Decode("/ip4/203.0.113.7/tcp/55555");

        peer.RecordObservedAddress(observed, "peer-a");
        peer.RecordObservedAddress(observed, "peer-a");
        Assert.That(peer.AdvertisedAddresses(), Is.Empty,
            "one peer reporting twice is still one voice");

        peer.RecordObservedAddress(observed, "peer-b");
        Assert.That(peer.AdvertisedAddresses(), Is.Empty,
            "two distinct voices are below the threshold");

        peer.RecordObservedAddress(observed, "peer-c");
        IReadOnlyList<Multiaddress> advertised = peer.AdvertisedAddresses();
        Assert.That(advertised.Select(a => a.ToString()),
            Is.EqualTo(new[] { "/ip4/203.0.113.7/tcp/4001" }),
            "the candidate carries the observed IP at the listener's own port, not the reporting connection's");
    }

    [Test]
    public void ObservedCandidate_KeepsTheListenShapesSuffix()
    {
        LocalPeer peer = PeerWith("/ip4/0.0.0.0/udp/4001/quic-v1");
        Multiaddress observed = Multiaddress.Decode("/ip4/203.0.113.7/tcp/55555");

        peer.RecordObservedAddress(observed, "peer-a");
        peer.RecordObservedAddress(observed, "peer-b");
        peer.RecordObservedAddress(observed, "peer-c");

        Assert.That(peer.AdvertisedAddresses().Select(a => a.ToString()),
            Is.EqualTo(new[] { "/ip4/203.0.113.7/udp/4001/quic-v1" }),
            "a candidate truncated to the bare port protocol names no transport and can never be dialed");
    }
}

public class WildcardExpansionTests
{
    [Test]
    public void WildcardListener_GainsConcreteInterfaceForms()
    {
        var peer = new LocalPeer(new Identity());
        Multiaddress wildcard = Multiaddress.Decode("/ip4/0.0.0.0/tcp/4001");
        peer.GetOrAddAddress(wildcard);

        peer.ExpandWildcardListener(wildcard);

        (Multiaddress address, PeerAddressAccounting accounting) = peer.Addresses
            .Single(pair => pair.Key.ToString() == "/ip4/127.0.0.1/tcp/4001");
        Assert.That(accounting.Sources, Is.EqualTo(AddressSource.Interface));
        Assert.That(peer.AdvertisedAddresses().Select(a => a.ToString()),
            Does.Contain("/ip4/127.0.0.1/tcp/4001"));
    }

    [Test]
    public void ConcreteListener_IsLeftAlone()
    {
        var peer = new LocalPeer(new Identity());
        Multiaddress concrete = Multiaddress.Decode("/ip4/192.0.2.1/tcp/4001");
        peer.GetOrAddAddress(concrete);

        peer.ExpandWildcardListener(concrete);

        Assert.That(peer.Addresses, Has.Count.EqualTo(1));
    }
}
