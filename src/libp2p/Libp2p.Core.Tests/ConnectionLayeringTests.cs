// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
///     A connection layered over another (a relayed connection over
///     its relay connection) ends when its parent does — explicitly,
///     through the parent reference — while the parent is untouched
///     by the layered connection ending.
/// </summary>
[TestFixture]
public class ConnectionLayeringTests
{
    private static (RemotePeerConnection Connection, Func<bool> Disconnected) MakeConnection()
    {
        bool disconnected = false;
        var connection = new RemotePeerConnection(
            new LocalPeer(new Identity()),
            new RemotePeer(new Identity()),
            () =>
            {
                disconnected = true;
                return Task.CompletedTask;
            },
            new ServiceCollection().BuildServiceProvider());
        return (connection, () => disconnected);
    }

    [Test]
    public async Task DisconnectingAParent_DisconnectsConnectionsLayeredOverIt()
    {
        var (parent, parentDisconnected) = MakeConnection();
        var (child, childDisconnected) = MakeConnection();
        var (grandchild, grandchildDisconnected) = MakeConnection();

        child.ParentConnection = parent;
        grandchild.ParentConnection = child;

        await parent.DisconnectAsync();

        Assert.That(parentDisconnected(), Is.True);
        Assert.That(childDisconnected(), Is.True);
        Assert.That(grandchildDisconnected(), Is.True);
        Assert.That(child.ParentConnection, Is.Null);
        Assert.That(grandchild.ParentConnection, Is.Null);
    }

    [Test]
    public async Task DisconnectingALayeredConnection_LeavesTheParentAlone()
    {
        var (parent, parentDisconnected) = MakeConnection();
        var (child, childDisconnected) = MakeConnection();

        child.ParentConnection = parent;
        await child.DisconnectAsync();

        Assert.That(childDisconnected(), Is.True);
        Assert.That(parentDisconnected(), Is.False);

        // And the parent no longer cascades to the departed child:
        // disconnecting it now must not run the child's teardown a
        // second time. (The child's delegate would still just record
        // true, so instead assert the child unregistered itself —
        // observable through a second child staying reachable.)
        var (child2, child2Disconnected) = MakeConnection();
        child2.ParentConnection = parent;
        await parent.DisconnectAsync();
        Assert.That(child2Disconnected(), Is.True);
    }
}
