// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     How a <see cref="KadClient"/> joins the DHT.
/// </summary>
public sealed class KadDhtOptions
{
    /// <summary>
    ///     Bootstrap address strings the client seeds its peer book
    ///     with at startup. Defaults to the public network's set
    ///     (<see cref="KadDht.BootstrapPeerAddresses"/>); set a
    ///     custom list for another network, or null to join by
    ///     hand (<see cref="KadClient.AddPeer"/>,
    ///     <see cref="KadClient.SeedBootstrapPeersAsync"/>).
    /// </summary>
    public IReadOnlyList<string>? BootstrapPeers { get; set; } = KadDht.BootstrapPeerAddresses;
}

public static class KadServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Kad client, wired for startup: when the
    ///     application calls StartLibp2pAsync after building its
    ///     provider, the client seeds its peer book from
    ///     <see cref="KadDhtOptions.BootstrapPeers"/> — the public
    ///     defaults unless configured otherwise — and its first
    ///     lookup awaits that seed rather than racing it.
    ///     Registering KadClient directly (without this) keeps the
    ///     client unseeded until told otherwise.
    ///     Also registers the serving half — the provider store
    ///     and the production peer table — so the node answers
    ///     other peers' queries and can provide content; a table
    ///     the application registered itself takes precedence
    ///     (TryAdd), so tests can substitute their own.
    /// </summary>
    public static IServiceCollection AddKadDht(this IServiceCollection services, Action<KadDhtOptions>? configure = null)
    {
        var options = new KadDhtOptions();
        configure?.Invoke(options);
        services
            .AddSingleton(options)
            .AddSingleton<KadClient>()
            .AddSingleton<ProviderStore>()
            .AddSingleton<ILibp2pStartup>(provider => provider.GetRequiredService<KadClient>());
        services.TryAddSingleton<IKadPeerTable, KadPeerTable>();
        return services;
    }
}
