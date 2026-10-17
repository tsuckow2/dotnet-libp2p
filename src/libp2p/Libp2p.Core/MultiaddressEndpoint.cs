// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     Turning an address into a dialable IP: literal /ip4/ and
///     /ip6/ addresses resolve to themselves; /dns/, /dns4/ and
///     /dns6/ names resolve through the system resolver (dns4 to an
///     IPv4 address, dns6 to IPv6, dns to whichever comes first).
///     Transports share this so a DNS-named peer — the public
///     network's bootstrap set is full of them — dials the same way
///     everywhere.
/// </summary>
public static class MultiaddressEndpoint
{
    public static async Task<IPAddress> ResolveIpAsync(Multiaddress addr)
    {
        if (addr.Get<IP4>()?.ToString() is string ip4)
        {
            return IPAddress.Parse(ip4);
        }

        if (addr.Get<IP6>()?.ToString() is string ip6)
        {
            return IPAddress.Parse(ip6);
        }

        string? host = addr.Get<DNS4>()?.ToString() ?? addr.Get<DNS6>()?.ToString() ?? addr.Get<DNS>()?.ToString();
        if (host is null)
        {
            throw new InvalidOperationException($"The address {addr} has no IP or DNS part to dial.");
        }

        AddressFamily? family = addr.Has<DNS4>() ? AddressFamily.InterNetwork
            : addr.Has<DNS6>() ? AddressFamily.InterNetworkV6
            : null;
        IPAddress[] resolved = await Dns.GetHostAddressesAsync(host);
        IPAddress? match = family is null
            ? resolved.FirstOrDefault()
            : resolved.FirstOrDefault(ip => ip.AddressFamily == family);
        return match ?? throw new InvalidOperationException($"The name {host} did not resolve to a usable address.");
    }
}
