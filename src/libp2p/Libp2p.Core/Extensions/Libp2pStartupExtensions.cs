// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core.Extensions;

/// <summary>
///     The node's bring-up: runs every registered
///     <see cref="ILibp2pStartup"/> component, in registration
///     order. The application owns the service provider, so the
///     library cannot see "DI is complete" on its own — this call
///     IS that moment. In a Generic Host application it maps
///     one-to-one onto an IHostedService's StartAsync.
/// </summary>
public static class Libp2pStartupExtensions
{
    public static async Task<IServiceProvider> StartLibp2pAsync(this IServiceProvider services, CancellationToken token = default)
    {
        foreach (ILibp2pStartup startup in services.GetServices<ILibp2pStartup>())
        {
            await startup.StartAsync(token);
        }
        return services;
    }
}
