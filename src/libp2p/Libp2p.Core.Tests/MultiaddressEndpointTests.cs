// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>Dial endpoint resolution: literal addresses, and DNS names (the public bootstrap set is named, not numbered).</summary>
[TestFixture]
public class MultiaddressEndpointTests
{
    [Test]
    public async Task Resolves_Ip4_Literally()
    {
        IPAddress ip = await MultiaddressEndpoint.ResolveIpAsync((Multiaddress)"/ip4/104.131.131.82/tcp/4001");
        Assert.That(ip, Is.EqualTo(IPAddress.Parse("104.131.131.82")));
    }

    [Test]
    public async Task Resolves_Dns4_ByName()
    {
        IPAddress ip = await MultiaddressEndpoint.ResolveIpAsync((Multiaddress)"/dns4/localhost/tcp/4001");
        Assert.That(ip, Is.EqualTo(IPAddress.Loopback));
    }

    [Test]
    public async Task Resolves_Dns_ByName()
    {
        IPAddress ip = await MultiaddressEndpoint.ResolveIpAsync((Multiaddress)"/dns/localhost/tcp/4001");
        Assert.That(ip, Is.Not.Null);
    }

    [Test]
    public void Rejects_An_Address_WithNoHostPart()
    {
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await MultiaddressEndpoint.ResolveIpAsync((Multiaddress)"/tcp/4001"));
    }
}
