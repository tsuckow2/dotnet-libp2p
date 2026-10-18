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
    ///     Installed by the switch on a listen chain's root factory:
    ///     creates the connection for a newly accepted transport
    ///     connection, wired to that listener.
    /// </summary>
    internal Func<IRemotePeerConnection>? ConnectionFactory { get; set; }

    public IRemotePeerConnection AcceptConnection()
    {
        return ConnectionFactory?.Invoke()
            ?? throw new InvalidOperationException("Only a listen chain's root factory can create connections for accepted transports.");
    }

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

    public IChannel SubDial(IRemotePeerConnection connection, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = new();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);

        _ = subProtocol.DialAsync(channel.Reverse, channelFactory, connection)
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

    public IChannel SubDialForRequest(IRemotePeerConnection connection, IChannelRequest req)
    {
        // A muxer opening a stream for a queued sub-dial request: dial
        // the layer above the muxer (the selector), telling it to
        // propose just the requested protocol. The request cannot ride
        // the connection — streams over one connection are concurrent —
        // so a selector that supports it is dialed through a per-stream
        // instance carrying the request. The muxer completes the
        // request itself when the stream channel closes, as it always
        // has; this method deliberately does not.
        IProtocol? subProtocol = SubProtocols.FirstOrDefault();
        Channel channel = new();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);

        IProtocol dialProtocol = subProtocol;
        if (subProtocol is ISpecificProtocolSelector)
        {
            dialProtocol = (IProtocol)ActivatorUtilities.CreateInstance(_serviceProvider, subProtocol.GetType());
            ((ISpecificProtocolSelector)dialProtocol).SpecificRequest = req;
            channelFactory = FactoryFor(dialProtocol) ?? channelFactory;
        }

        _ = dialProtocol.DialAsync(channel.Reverse, channelFactory, connection)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialFailed(dialProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await channel.CloseAsync();
            });

        return channel;
    }

    public IChannel SubListen(IRemotePeerConnection connection, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = new();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);


        _ = subProtocol.ListenAsync(channel.Reverse, channelFactory, connection)
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

    public Task SubDialAndBind(IChannel parent, IRemotePeerConnection connection,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);

        return subProtocol.DialAsync(((Channel)parent), channelFactory, connection)
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

    public Task SubListenAndBind(IChannel parent, IRemotePeerConnection connection,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        ChannelFactory? channelFactory = FactoryFor(subProtocol);

        return subProtocol.ListenAsync(((Channel)parent), channelFactory, connection)
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
