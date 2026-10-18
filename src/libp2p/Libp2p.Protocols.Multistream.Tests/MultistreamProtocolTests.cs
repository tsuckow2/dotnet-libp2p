// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols;

namespace Libp2p.Protocols.Multistream.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class MultistreamProtocolTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IRemotePeerConnection connection = Substitute.For<IRemotePeerConnection>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        channelFactory.SubProtocols.Returns(new[] { proto1 });
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IRemotePeerConnection>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, connection);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("proto1");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        // The bind happens on the protocol's continuation after it
        // reads the selection answer; give it the same moment the
        // specific-request variant of this test already takes.
        await Task.Delay(30);
        _ = channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, connection, proto1);
        await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake_With_SpecificRequest()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IRemotePeerConnection connection = Substitute.For<IRemotePeerConnection>();
        IChannelRequest channelRequest = Substitute.For<IChannelRequest>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        channelRequest.SubProtocol.Returns(proto1);
        IChannel upChannel = new TestChannel();

        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IRemotePeerConnection>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);

        MultistreamProtocol proto = new() { SpecificRequest = channelRequest };
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, connection);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("proto1");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        await Task.Delay(30);
        _ = channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, connection, proto1);
        await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForUnknownProtocol()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IRemotePeerConnection connection = Substitute.For<IRemotePeerConnection>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        channelFactory.SubProtocols.Returns(new[] { proto1 });

        MultistreamProtocol proto = new();
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("proto2");
        });

        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, connection);

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));

        _ = channelFactory.DidNotReceive().SubDialAndBind(downChannelFromProtocolPov, connection, proto1);
    }

    [Test]
    public async Task Test_ConnectionEstablished_ForAnyOfProtocols()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IRemotePeerConnection connection = Substitute.For<IRemotePeerConnection>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto2.Id.Returns("proto2");
        channelFactory.SubProtocols.Returns(new[] { proto1, proto2 });
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IRemotePeerConnection>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, connection);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("na");
            await downChannel.WriteLineAsync("proto2");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto1.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto2.Id));

        await Task.Delay(30);
        _ = channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, connection, proto2);
        await upChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForBadProtocol()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IRemotePeerConnection connection = Substitute.For<IRemotePeerConnection>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto2");
        channelFactory.SubProtocols.Returns(new[] { proto1, proto2 });

        MultistreamProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, connection);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("na1");
            await downChannel.WriteLineAsync("proto2");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto1.Id));
        _ = channelFactory.DidNotReceiveWithAnyArgs().SubDialAndBind(null!, null!, (IProtocol)null!);
    }
}
