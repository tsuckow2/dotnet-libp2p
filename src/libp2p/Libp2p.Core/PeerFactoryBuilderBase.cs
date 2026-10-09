// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public static class PeerFactoryBuilderBase
{
    internal static TProtocol CreateProtocolInstance<TProtocol>(IServiceProvider serviceProvider, TProtocol? instance = default)
    {
        return instance ?? ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(serviceProvider);
    }
}

/// <summary>
///     Base for peer factory builders. A builder is just a set of protocol
///     registrations by role plus the service provider to create them from;
///     <see cref="Build"/> merges those registrations with whatever DI
///     already provides into the one registry and hands it, with the local
///     peer, to the switch.
/// </summary>
public abstract class PeerFactoryBuilderBase<TBuilder> : IPeerFactoryBuilder
    where TBuilder : PeerFactoryBuilderBase<TBuilder>, IPeerFactoryBuilder
{
    private readonly List<ITransport> _transports = [];
    private readonly List<IMuxerProtocol> _muxerProtocols = [];
    private readonly List<ISecurityProtocol> _securityProtocols = [];
    private readonly List<IIdentityProtocol> _identityProtocols = [];
    private readonly List<IProtocol> _appLayerProtocols = [];
    private readonly List<IProtocolRef> _managedProtocols = [];

    protected PeerFactoryBuilderBase(IServiceProvider? serviceProvider = default)
    {
        ServiceProvider = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    }

    protected IServiceProvider ServiceProvider { get; }

    public IEnumerable<IProtocol> AppLayerProtocols => _appLayerProtocols;

    protected TBuilder AddTransport<TProtocol>(TProtocol? instance = default) where TProtocol : class, ITransport
    {
        _transports.Add(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider, instance));
        return (TBuilder)this;
    }

    protected TBuilder AddMuxerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : class, IMuxerProtocol
    {
        _muxerProtocols.Add(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider, instance));
        return (TBuilder)this;
    }

    protected TBuilder AddSecurityProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : class, ISecurityProtocol
    {
        _securityProtocols.Add(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider, instance));
        return (TBuilder)this;
    }

    protected void ClearSecurityProtocols()
    {
        _securityProtocols.Clear();
    }

    protected TBuilder AddIdentityProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : class, IIdentityProtocol
    {
        _identityProtocols.Add(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider, instance));
        return (TBuilder)this;
    }

    public IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
    {
        _appLayerProtocols.Add(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider, instance));
        return this;
    }

    public IPeerFactoryBuilder AddManagedProtocol<TProtocol, TOut>(ProtocolRef<TProtocol, TOut> protocolRef) where TProtocol : IProtocol<TOut>
    {
        _managedProtocols.Add(protocolRef);
        return this;
    }

    public IPeerFactory Build()
    {
        ILocalPeer localPeer = ServiceProvider.GetService<ILocalPeer>() ?? new LocalPeer(new Identity());
        IProtocolRegistry diRegistry = ServiceProvider.GetService<IProtocolRegistry>() ?? new DIProtocolRegistry(ServiceProvider);
        var builderRegistry = new InMemoryProtocolRegistry
        {
            Transports = _transports,
            MuxerProtocols = _muxerProtocols,
            SecurityProtocols = _securityProtocols,
            IdentityProtocols = _identityProtocols,
            OtherProtocols = _appLayerProtocols,
        };
        return new ManagedPeerSwitch(localPeer, new CompositeProtocolRegistry(diRegistry, builderRegistry), ServiceProvider);
    }
}
