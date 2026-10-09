// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Runtime.Versioning;

namespace Nethermind.Libp2p.Stack;

public static class ServiceProviderExtensions
{
    public static IServiceCollection AddLibp2pLocalPeer(this IServiceCollection services, ILocalPeer localPeer)
    {
        return services.AddSingleton<ILocalPeer>(localPeer);
    }

    [RequiresPreviewFeatures]
    public static IServiceCollection AddLibp2p(this IServiceCollection services, Func<ILibp2pPeerFactoryBuilder, IPeerFactoryBuilder> factorySetup)
    {
        services.AddSingleton<DIProtocolRegistry>();
        services.AddSingleton<IProtocolRegistry>(sp => sp.GetRequiredService<DIProtocolRegistry>());

        return services
            // One builder instance behind every registration: factorySetup
            // runs exactly once per scope.
            .AddScoped<Libp2pPeerFactoryBuilder>(sp => (Libp2pPeerFactoryBuilder)factorySetup(new Libp2pPeerFactoryBuilder(sp)))
            .AddScoped<ILibp2pPeerFactoryBuilder>(sp => sp.GetRequiredService<Libp2pPeerFactoryBuilder>())
            .AddScoped<IPeerFactoryBuilder>(sp => sp.GetRequiredService<Libp2pPeerFactoryBuilder>())
            .AddScoped(sp => sp.GetRequiredService<IPeerFactoryBuilder>().Build())
            .AddScoped<PubsubRouter>()
            .AddScoped<MultiplexerSettings>()
            ;
    }
}
