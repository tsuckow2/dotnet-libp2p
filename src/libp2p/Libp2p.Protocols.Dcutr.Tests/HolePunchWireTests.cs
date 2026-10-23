// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Protocols.Dcutr.Tests;

[TestFixture]
public class HolePunchWireTests
{
    [Test]
    public async Task Connect_RoundTrips_WithBinaryAddresses()
    {
        var channel = new TestChannel();
        var message = new HolePunchMessage
        {
            Type = HolePunchMessageType.Connect,
            ObsAddrs =
            [
                MultiaddressWire.Encode(Multiaddress.Decode("/ip4/203.0.113.7/udp/4001/quic-v1")),
                MultiaddressWire.Encode(Multiaddress.Decode("/ip4/203.0.113.7/tcp/4001")),
            ],
        };

        // The test channel is a rendezvous: a write completes only
        // once the reader has consumed it, so the write runs
        // concurrently with the read, as it would between two peers.
        Task writing = HolePunchWire.WriteAsync(channel, message);
        HolePunchMessage read = await HolePunchWire.ReadAsync(channel.Reverse());
        await writing;

        Assert.That(read.Type, Is.EqualTo(HolePunchMessageType.Connect));
        Assert.That(read.ObsAddrs, Has.Count.EqualTo(2));
        Assert.That(MultiaddressWire.Decode(read.ObsAddrs[0]).ToString(),
            Is.EqualTo("/ip4/203.0.113.7/udp/4001/quic-v1"));
        Assert.That(MultiaddressWire.Decode(read.ObsAddrs[1]).ToString(),
            Is.EqualTo("/ip4/203.0.113.7/tcp/4001"));
    }

    [Test]
    public async Task Sync_RoundTrips_WithNoAddresses()
    {
        var channel = new TestChannel();

        Task writing = HolePunchWire.WriteAsync(channel,
            new HolePunchMessage { Type = HolePunchMessageType.Sync });
        HolePunchMessage read = await HolePunchWire.ReadAsync(channel.Reverse());
        await writing;

        Assert.That(read.Type, Is.EqualTo(HolePunchMessageType.Sync));
        Assert.That(read.ObsAddrs, Is.Empty);
    }

    [Test]
    public async Task OversizeMessage_IsRefused_BeforePayloadIsRead()
    {
        var channel = new TestChannel();
        // Announce a payload just over the 4 KiB cap; nothing follows
        // it, so a reader that trusted the prefix would block — the
        // refusal must come from the length alone.
        Task writing = ((IChannel)channel)
            .WriteVarintAsync(HolePunchWire.MaxMessageSize + 1).AsTask();

        Assert.ThrowsAsync<PeerConnectionException>(
            async () => await HolePunchWire.ReadAsync(channel.Reverse()));
        await writing;
    }
}
