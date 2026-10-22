// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net.NetworkInformation;
using System.Net.Sockets;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     The local peer's advertised address set: what this node tells
///     the network it can be reached at, derived from the address
///     book by evidence. Policy lives here, at the consumer's edge —
///     the book itself only records facts.
///
///     An address is advertised when it is concrete (a wildcard
///     listen template binds every interface but reaches none) and
///     either it is not solely an observed candidate, or enough
///     distinct peers have vouched for the observation. Observed
///     addresses are how a node behind NAT learns its public form;
///     a single report can be a lie or a fluke of one connection's
///     mapping, so they must earn their way in by agreement.
/// </summary>
public static class LocalPeerAddresses
{
    /// <summary>Distinct proven peers that must report an observed address before it is advertised.</summary>
    public const int ObservedVouchThreshold = 3;

    /// <summary>
    ///     Gets the accounting for an address equal in value to the
    ///     given one, adding it when the book has none. The vendored
    ///     Multiaddress carries no value equality, so book lookups
    ///     for a freshly parsed address must compare string forms —
    ///     a plain GetOrAdd would file the same address once per
    ///     parse, each copy with its own accounting.
    /// </summary>
    public static PeerAddressAccounting GetOrAddByValue(this IPeer peer, Multiaddress address)
    {
        string form = address.ToString();
        foreach ((Multiaddress known, PeerAddressAccounting accounting) in peer.Addresses)
        {
            if (known.ToString() == form)
            {
                return accounting;
            }
        }
        return peer.GetOrAddAddress(address);
    }

    public static IReadOnlyList<Multiaddress> AdvertisedAddresses(this ILocalPeer peer)
    {
        var advertised = new List<Multiaddress>();
        var seen = new HashSet<string>();
        foreach ((Multiaddress address, PeerAddressAccounting accounting) in peer.Addresses)
        {
            if (HasUnspecifiedAddress(address) || !seen.Add(address.ToString()))
            {
                continue;
            }

            bool observedOnly = accounting.Sources != AddressSource.None
                && (accounting.Sources & ~AddressSource.Observed) == AddressSource.None;
            if (observedOnly && accounting.VouchCount < ObservedVouchThreshold)
            {
                continue;
            }

            advertised.Add(address);
        }
        return advertised;
    }

    /// <summary>
    ///     Records a peer's report of the address it observes this
    ///     node at — the node's public, post-NAT form as seen on
    ///     that connection. The report vouches for one candidate per
    ///     listen shape in the book: the observed IP at the shape's
    ///     own listen port, since the observed port belongs to the
    ///     reporting connection, not to any listener. Candidates
    ///     enter the book immediately (marked Observed, vouched by
    ///     the reporter); <see cref="AdvertisedAddresses"/> decides
    ///     when they have earned advertisement.
    /// </summary>
    public static void RecordObservedAddress(this ILocalPeer peer, Multiaddress observed, string reporterPeerId)
    {
        string? observedIp4 = observed.Protocols.OfType<IP4>().FirstOrDefault()?.ToString();
        string? observedIp6 = observed.Protocols.OfType<IP6>().FirstOrDefault()?.ToString();
        if (observedIp4 is null && observedIp6 is null)
        {
            return;
        }

        var candidates = new HashSet<string>();
        foreach (Multiaddress address in peer.Addresses.Keys.ToArray())
        {
            string? candidate = CandidateFor(address, observedIp4, observedIp6);
            if (candidate is not null && candidates.Add(candidate))
            {
                peer.GetOrAddByValue(Multiaddress.Decode(candidate))
                    .RecordVouch(reporterPeerId, AddressSource.Observed);
            }
        }
    }

    /// <summary>
    ///     Expands a bound listener's address over the machine's
    ///     interfaces: a wildcard address binds every interface but
    ///     can be advertised to none of them, so each up interface
    ///     contributes its concrete form of the same listen shape
    ///     (same port and suffix) to the book, marked as
    ///     interface-sourced. Non-wildcard listeners already name
    ///     their address; nothing is added for them.
    /// </summary>
    public static void ExpandWildcardListener(this ILocalPeer peer, Multiaddress listenerAddress)
    {
        string[] parts = listenerAddress.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("ip4" or "ip6"))
        {
            return;
        }

        bool wildcard = parts[0] == "ip4" ? parts[1] == "0.0.0.0" : parts[1] == "::";
        if (!wildcard)
        {
            return;
        }

        AddressFamily family = parts[0] == "ip4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        foreach (string interfaceIp in InterfaceAddresses(family))
        {
            parts[1] = interfaceIp;
            peer.GetOrAddByValue(Multiaddress.Decode("/" + string.Join("/", parts)))
                .RecordSource(AddressSource.Interface);
        }
    }

    private static IEnumerable<string> InterfaceAddresses(AddressFamily family)
    {
        var seen = new HashSet<string>();
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == family && seen.Add(unicast.Address.ToString()))
                {
                    yield return unicast.Address.ToString();
                }
            }
        }
    }

    private static string? CandidateFor(Multiaddress listenShape, string? observedIp4, string? observedIp6)
    {
        string[] parts = listenShape.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        // A listen shape: <ip-proto>/<ip>/<port-proto>/<port>[/...]
        if (parts.Length < 4)
        {
            return null;
        }

        string? observedIp = parts[0] switch
        {
            "ip4" => observedIp4,
            "ip6" => observedIp6,
            _ => null,
        };
        if (observedIp is null || parts[1] == observedIp)
        {
            return null;
        }

        if (parts[2] is not ("tcp" or "udp"))
        {
            return null;
        }

        return $"/{parts[0]}/{observedIp}/{parts[2]}/{parts[3]}";
    }

    private static bool HasUnspecifiedAddress(Multiaddress address)
    {
        return address.Protocols.OfType<IP4>().FirstOrDefault()?.ToString() == "0.0.0.0"
            || address.Protocols.OfType<IP6>().FirstOrDefault()?.ToString() == "::";
    }
}
