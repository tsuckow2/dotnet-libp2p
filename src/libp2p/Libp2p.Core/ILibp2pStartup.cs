// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
///     A singleton component with startup work: something to do
///     once the service provider exists and the node is being
///     brought up, before (or while) it serves. Registered
///     components are started by
///     <see cref="Extensions.Libp2pStartupExtensions.StartLibp2pAsync"/>,
///     which the application calls once after building its provider.
///
///     This is deliberately NOT part of either protocol tier:
///     protocol instances are created per connection by the switch,
///     so a DI-complete moment does not exist in their lifetime.
///     Startup belongs to the singletons — clients, routers,
///     services — that DI keeps exactly one of.
///
///     StartAsync BEGINS the component's work and returns promptly:
///     slow I/O (a DNS lookup, a first dial) must not hold every
///     other component's start hostage. A component whose startup
///     completes asynchronously keeps the work's task and has its
///     own consumers await it, so first use can never race an
///     in-flight start. (KadClient's bootstrap seed is the
///     reference example.)
/// </summary>
public interface ILibp2pStartup
{
    Task StartAsync(CancellationToken token = default);
}
