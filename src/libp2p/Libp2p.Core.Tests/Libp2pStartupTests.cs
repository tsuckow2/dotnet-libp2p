// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
///     The startup pass: every registered ILibp2pStartup runs,
///     once, in registration order — and a provider with none
///     registered starts cleanly.
/// </summary>
[TestFixture]
public class Libp2pStartupTests
{
    private sealed class Recorder(string name, List<string> log) : ILibp2pStartup
    {
        public Task StartAsync(CancellationToken token = default)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task StartLibp2pAsync_RunsEveryComponent_InRegistrationOrder()
    {
        var log = new List<string>();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILibp2pStartup>(new Recorder("first", log))
            .AddSingleton<ILibp2pStartup>(new Recorder("second", log))
            .BuildServiceProvider();

        IServiceProvider returned = await services.StartLibp2pAsync();

        Assert.That(log, Is.EqualTo(new[] { "first", "second" }));
        Assert.That(returned, Is.SameAs(services));
    }

    [Test]
    public async Task StartLibp2pAsync_NoComponents_IsANoOp()
    {
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        await services.StartLibp2pAsync();
    }
}
