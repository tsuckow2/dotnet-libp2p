// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Dcutr;

/// <summary>
///     Address selection for hole punching, at the consumer's edge —
///     the book only records facts. Every set entering or leaving
///     the exchange is built from evidence, never raw candidates:
///     what we send is our earned advertised set; what we dial is
///     what we have reason to believe is direct and dialable; what
///     we accept from the peer is checked before we touch it.
/// </summary>
public static class DcutrAddresses
{
    /// <summary>Addresses offered in one CONNECT: more than this is noise the 4 KiB cap need not carry.</summary>
    public const int MaxOfferedAddresses = 8;

    /// <summary>
    ///     Our own addresses for a CONNECT message: the local peer's
    ///     earned advertised set — the addresses the book's evidence
    ///     says we can actually be reached at — minus anything that
    ///     is not a direct punch target: relayed forms (the relay is
    ///     how we got here, not what we upgrade to), listen
    ///     templates that were never bound (port zero), and our own
    ///     /p2p suffix (the dialer names the destination itself).
    ///     QUIC first: UDP simultaneous open is the reliable punch.
    /// </summary>
    public static IReadOnlyList<Multiaddress> OwnAddresses(ILocalPeer localPeer)
    {
        return RankDirect(localPeer.AdvertisedAddresses()
            .Where(IsDialableForm)
            .Select(WithoutPeerId));
    }

    /// <summary>
    ///     A peer's known addresses for the unilateral upgrade
    ///     attempt: the book's evidence-bearing direct forms. An
    ///     address qualifies when a dial once succeeded there, or
    ///     the peer itself vouched for the form (its Identify claim
    ///     or an observed report) — bare referrals and failures
    ///     without any claim are not worth a direct attempt.
    /// </summary>
    public static IReadOnlyList<Multiaddress> KnownDirectAddresses(IPeer peer)
    {
        var candidates = new List<(Multiaddress Address, PeerAddressAccounting Accounting)>();
        foreach ((Multiaddress address, PeerAddressAccounting accounting) in peer.Addresses)
        {
            if (!IsDialableForm(address))
            {
                continue;
            }

            bool vouched = (accounting.Sources & (AddressSource.SelfClaimed | AddressSource.Observed)) != 0;
            if (accounting.SuccessfulDials == 0 && !vouched)
            {
                continue;
            }

            candidates.Add((WithoutPeerId(address), accounting));
        }

        return candidates
            .OrderBy(pair => TransportRank(pair.Address))
            .ThenByDescending(pair => pair.Accounting.LastSuccessAt ?? DateTimeOffset.MinValue)
            .ThenBy(pair => pair.Accounting.FailedDials)
            .Select(pair => pair.Address)
            .Take(MaxOfferedAddresses)
            .ToList();
    }

    /// <summary>
    ///     Sanitizes addresses decoded from a peer's CONNECT: they
    ///     are the sender's claim about itself, and claims are
    ///     checked before dialing. Kept are concrete direct forms;
    ///     dropped are circuit forms (upgrading to what we already
    ///     have), listen templates (nothing is bound there),
    ///     addresses naming someone else's /p2p (dialing a third
    ///     party on this peer's say-so is not an upgrade), and
    ///     anything unparseable the caller could not decode.
    /// </summary>
    public static IReadOnlyList<Multiaddress> ReceivedAddresses(
        IEnumerable<Multiaddress> decoded, PeerId senderPeerId)
    {
        var accepted = new List<Multiaddress>();
        foreach (Multiaddress address in decoded)
        {
            if (!IsDialableForm(address))
            {
                continue;
            }

            P2P? terminal = address.TerminalP2P();
            if (terminal is not null && terminal.ToString() != senderPeerId.ToString())
            {
                continue;
            }

            accepted.Add(terminal is null ? address : WithoutPeerId(address));
        }
        return RankDirect(accepted);
    }

    private static bool IsDialableForm(Multiaddress address)
    {
        if (address.Protocols.Any(protocol => protocol.Name == "p2p-circuit"))
        {
            return false;
        }

        // A wildcard form binds every interface but reaches none.
        if (address.Protocols.OfType<IP4>().FirstOrDefault()?.ToString() == "0.0.0.0"
            || address.Protocols.OfType<IP6>().FirstOrDefault()?.ToString() == "::")
        {
            return false;
        }

        // A listen template that was never bound carries port zero;
        // nothing answers there.
        if (address.Protocols.FirstOrDefault(protocol => protocol.Name is "tcp" or "udp")
            is Number ported && ported.Port == 0)
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<Multiaddress> RankDirect(IEnumerable<Multiaddress> addresses)
    {
        var seen = new HashSet<string>();
        return addresses
            .Where(address => seen.Add(address.ToString()))
            .OrderBy(TransportRank)
            .Take(MaxOfferedAddresses)
            .ToList();
    }

    /// <summary>
    ///     QUIC first: the simultaneous UDP open is the reliable
    ///     punch; plain UDP follows; TCP last (a simultaneous TCP
    ///     open needs same-port SYN crossing the transports here do
    ///     not perform, so TCP attempts are ordinary direct dials).
    /// </summary>
    private static int TransportRank(Multiaddress address)
    {
        var protocols = address.Protocols.ToArray();
        if (protocols.Any(protocol => protocol.Name is "quic" or "quic-v1"))
        {
            return 0;
        }
        if (protocols.Any(protocol => protocol.Name is "udp"))
        {
            return 1;
        }
        if (protocols.Any(protocol => protocol.Name is "tcp"))
        {
            return 2;
        }
        return 3;
    }

    private static Multiaddress WithoutPeerId(Multiaddress address)
    {
        string form = address.ToString();
        int lastSlash = form.LastIndexOf('/');
        int protocolSlash = lastSlash > 0 ? form.LastIndexOf('/', lastSlash - 1) : -1;
        return protocolSlash > 0 && form[(protocolSlash + 1)..lastSlash] == "p2p"
            ? Multiaddress.Decode(form[..protocolSlash])
            : address;
    }
}
