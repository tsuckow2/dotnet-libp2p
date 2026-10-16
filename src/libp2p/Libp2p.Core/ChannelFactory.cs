// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core;

public class ChannelFactory : IChannelFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory? _loggerFactory;
    private IDictionary<IProtocol, IChannelFactory> _factories;
    private readonly ILogger? _logger;

    public ChannelFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = _serviceProvider.GetService<ILoggerFactory>();
        _logger = _loggerFactory?.CreateLogger<ChannelFactory>();
    }

    public IEnumerable<IProtocol> SubProtocols => _factories.Keys;

    /// <summary>
    ///     The factory above a protocol: found by instance when the
    ///     protocol is one of this factory's own (a stack layer), else
    ///     by protocol id — a per-connection dial carries an instance
    ///     created for that dial, which is the same protocol but not
    ///     the same object as the registry's.
    /// </summary>
    private ChannelFactory? FactoryFor(IProtocol? subProtocol)
    {
        if (subProtocol is null)
        {
            return null;
        }

        if (_factories.TryGetValue(subProtocol, out IChannelFactory? factory))
        {
            return factory as ChannelFactory;
        }

        return _factories.FirstOrDefault(pair => pair.Key.Id == subProtocol.Id).Value as ChannelFactory;
    }

    private void Complete(IChannelRequest? req, Task task)
    {
        if (task.IsCompletedSuccessfully)
        {
            req?.CompletionSource?.TrySetResult();
        }
        else
        {
            // A failed protocol must fault its request: completing it
            // successfully would report a dial that never happened.
            req?.CompletionSource?.TrySetException(
                (Exception?)task.Exception ?? new InvalidOperationException("The protocol did not complete."));
        }
    }

    public IChannel SubDial(IPeerContext context, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = new();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);


        _ = subProtocol.DialAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialFailed(subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await channel.CloseAsync();

                Complete(req, task);
            });

        return channel;
    }

    public IChannel SubListen(IPeerContext context, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = new();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);


        _ = subProtocol.ListenAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.ListenFailed(subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await channel.CloseAsync();

                Complete(req, task);
            });

        return channel;
    }

    public Task SubDialAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);

        return subProtocol.DialAsync(((Channel)parent), channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialAndBindFailed(subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await parent.CloseAsync();

                Complete(req, task);
            });
    }

    public Task SubListenAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);

        return subProtocol.ListenAsync(((Channel)parent), channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.ListenFailed(subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await parent.CloseAsync();

                Complete(req, task);
            });
    }

    public ChannelFactory Setup(IDictionary<IProtocol, IChannelFactory> factories)
    {
        _factories = factories;
        return this;
    }
}
