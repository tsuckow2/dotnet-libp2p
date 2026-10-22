// SPDX-FileCopyrightText: 2026 Thomas Suckow
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Ipfs.Kad;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Status;

public static class StatusServiceCollectionExtensions
{
    /// <summary>
    ///     Adds the live status page: a <see cref="NodeStatusProvider"/>
    ///     over the node's local peer, switch, and (when registered)
    ///     Kad DHT, served by a <see cref="StatusServer"/> that
    ///     starts with the node as an <see cref="ILibp2pStartup"/>.
    /// </summary>
    public static IServiceCollection AddIpfsStatus(
        this IServiceCollection services, Action<StatusServerOptions>? configure = null)
    {
        var options = new StatusServerOptions();
        configure?.Invoke(options);
        services
            .AddSingleton(options)
            .AddSingleton(provider => new NodeStatusProvider(
                provider.GetRequiredService<ILocalPeer>(),
                provider.GetService<IManagedPeerSwitch>(),
                provider.GetService<KadClient>(),
                provider.GetService<ProviderStore>()))
            .AddSingleton<StatusServer>()
            .AddSingleton<ILibp2pStartup>(provider => provider.GetRequiredService<StatusServer>());
        return services;
    }
}
