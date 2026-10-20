// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Circuit;

/// <summary>
///     The Circuit Relay v2 transport: it serves the /p2p-circuit
///     component of an address. An address is a layered path and this
///     transport serves its layer by cutting the address at the
///     circuit boundary, dialing the relay prefix back through the
///     switch (a full stack of its own), and asking the relay to
///     connect to the destination the address terminates at. The
///     stack above then runs end-to-end with the destination over
///     the relayed stream — Noise proves the destination's identity
///     against the connection's expected peer id, so the relay,
///     an untrusted pipe, cannot substitute another peer.
///
///     Listening works the other way: reserve on the relay named by
///     the listen address, and accept the inbound streams the relay
///     delivers over the stop protocol as new connections.
/// </summary>
public class CircuitTransport(IServiceProvider serviceProvider, CircuitJobs jobs, ILoggerFactory? loggerFactory = null) : ITransport, IProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<CircuitTransport>();
    private IManagedPeerSwitch? _switch;

    // The builder creates this transport, so the switch can only be
    // resolved lazily, once construction has finished; resolving it
    // in the constructor deadlocks the DI scope building the stack
    // (the same reason Identify and the Kad client resolve lazily).
    private IManagedPeerSwitch Switch => _switch ??= serviceProvider.GetRequiredService<IManagedPeerSwitch>();

    public string Id => "circuit-v2";

    public string AddressProtocol => "p2p-circuit";

    // ---- dial -----------------------------------------------------------

    public async Task DialAsync(IChannel signalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(channelFactory);
        using var lifetime = new CancellationTokenSource();
        signalingChannel.GetAwaiter().OnCompleted(() => lifetime.Cancel());

        Multiaddress addr = connection.RemotePeer.Address;
        (Multiaddress relayAddress, PeerId destination) = Cut(addr);

        var job = HopJob.Connect(destination);
        Task hopTask = await OpenHopAsync(relayAddress, job, lifetime.Token);

        connection.RemoteEndpoint = addr;
        connection.LocalEndpoint = job.RelayConnection?.LocalEndpoint;

        IChannel upChannel = channelFactory.SubDial(connection);
        job.PumpTo.TrySetResult(upChannel);

        try
        {
            await hopTask;
        }
        catch (Exception e)
        {
            _logger?.LogDebug(e, "The relayed connection to {destination} ended", destination);
        }
        _ = upChannel.CloseAsync();
    }

    // ---- listen ---------------------------------------------------------

    public async Task ListenAsync(IChannel signalingChannel, IChannelFactory? channelFactory, IRemotePeerConnection shell)
    {
        ArgumentNullException.ThrowIfNull(channelFactory);
        using var lifetime = new CancellationTokenSource();
        signalingChannel.GetAwaiter().OnCompleted(() => lifetime.Cancel());

        // The listen address names the relay to reserve on:
        // <relay>/p2p-circuit/p2p/<us>. The switch stamps the local
        // id only onto addresses with no /p2p at all — a circuit
        // address already carries the relay's — so the transport
        // completes the advertised form itself.
        Multiaddress addr = shell.LocalPeer.Address;
        PeerId self = shell.LocalPeer.Identity.PeerId;
        if (addr.TerminalP2P() is not { } terminal || new PeerId(terminal.ToString()) != self)
        {
            addr = addr.Add<P2P>(self.ToString());
        }
        (Multiaddress relayAddress, _) = Cut(addr);
        shell.LocalEndpoint = addr;

        jobs.RegisterListener(shell.LocalPeer.Identity.PeerId,
            acceptance => AcceptRelayedAsync(channelFactory, acceptance));
        try
        {
            HopJob reservation = HopJob.Reserve();
            await OpenHopAsync(relayAddress, reservation, lifetime.Token);
            RestoreListenAddress(shell.LocalPeer, addr);
            shell.ListenerReady();
            _logger?.LogDebug("Listening through relay {relay} as {address}", relayAddress, addr);

            await MaintainReservationAsync(relayAddress, addr, shell.LocalPeer, reservation, lifetime.Token);
        }
        finally
        {
            jobs.UnregisterListener(shell.LocalPeer.Identity.PeerId);
        }
    }

    /// <summary>
    ///     Dialing the relay rewrites the local peer's primary address
    ///     to the dial's own endpoint (the socket transports state
    ///     their endpoint there). A circuit listener's address is the
    ///     circuit address, so the transport states it back after
    ///     every relay dial made on the listener's behalf.
    /// </summary>
    private static void RestoreListenAddress(ILocalPeer localPeer, Multiaddress addr)
    {
        if (localPeer is LocalPeer concrete)
        {
            concrete.Address = addr;
        }
    }

    /// <summary>
    ///     Keeps the reservation alive: renew before the relay's
    ///     expiry (the new reservation is established before the old
    ///     stream is released), retrying through relay failures for as
    ///     long as the listener lives.
    /// </summary>
    private async Task MaintainReservationAsync(Multiaddress relayAddress, Multiaddress listenAddress, ILocalPeer localPeer, HopJob current, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long ttl = current.ReservationExpireUnix > now ? current.ReservationExpireUnix - now : 3600;
            // Renew ten minutes early; never spin.
            var wait = TimeSpan.FromSeconds(Math.Clamp(ttl - 600, 30, ttl));
            try
            {
                await Task.Delay(wait, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                HopJob next = HopJob.Reserve();
                await OpenHopAsync(relayAddress, next, token);
                RestoreListenAddress(localPeer, listenAddress);
                current.Closed.TrySetResult();
                current = next;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger?.LogWarning(e, "Renewing the relay reservation failed; retrying");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        current.Closed.TrySetResult();
    }

    private async Task AcceptRelayedAsync(IChannelFactory factory, StopAcceptance acceptance)
    {
        IRemotePeerConnection connection = factory.AcceptConnection();
        connection.RemoteEndpoint = acceptance.SourceAddress;
        connection.RemotePeer.Address = acceptance.SourceAddress;

        IChannel upChannel = factory.SubListen(connection);
        await ChannelPump.BothWaysAsync(acceptance.Channel, upChannel);
        _ = upChannel.CloseAsync();
    }

    // ---- the hop exchange ----------------------------------------------

    /// <summary>
    ///     Dials the relay through the switch and performs the queued
    ///     hop job on that connection, returning the hop protocol's
    ///     task (its lifetime is the stream's). Requests to one relay
    ///     are serialized around the enqueue-and-claim sequence so a
    ///     claim always takes its own job.
    /// </summary>
    private async Task<Task> OpenHopAsync(Multiaddress relayAddress, HopJob job, CancellationToken token)
    {
        IRemotePeerConnection relayConnection = await Switch.DialAsync(relayAddress, token);
        PeerId relayId = relayConnection.RemotePeer.Identity.PeerId;

        SemaphoreSlim gate = jobs.HopGate(relayId);
        await gate.WaitAsync(token);
        try
        {
            jobs.EnqueueHop(relayId, job);
            Task hopTask = relayConnection.DialAsync<HopProtocol>(token);
            await job.Established.Task.WaitAsync(token);
            return hopTask;
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- raw transport tier ---------------------------------------------

    public async Task<IRemoteComm> DialAsync(Multiaddress address)
    {
        (Multiaddress relayAddress, PeerId destination) = Cut(address);
        var job = HopJob.Connect(destination, raw: true);
        await OpenHopAsync(relayAddress, job, CancellationToken.None);
        IChannel stream = await job.Stream.Task;

        var connection = new GenericRemoteConnection(address, () =>
        {
            job.Closed.TrySetResult();
            return Task.CompletedTask;
        });
        return new GenericRemoteComm(false, stream, connection);
    }

    public Task ListenAsync(Multiaddress localAddress, Action<IChannel, Multiaddress> onConnection, CancellationToken token)
    {
        // A raw listen has no node identity to hold a reservation
        // against; circuit listening is a switch-stack operation.
        throw new NotSupportedException("Circuit listening requires the switch stack.");
    }

    // ---- addresses --------------------------------------------------------

    /// <summary>
    ///     Splits a circuit address at its boundary: everything before
    ///     /p2p-circuit is the relay's address (itself dialed through
    ///     the switch); the peer the whole address names is its
    ///     terminal /p2p.
    /// </summary>
    internal static (Multiaddress RelayAddress, PeerId Destination) Cut(Multiaddress address)
    {
        string full = address.ToString();
        int boundary = full.IndexOf("/p2p-circuit", StringComparison.Ordinal);
        if (boundary < 0)
        {
            throw new ArgumentException($"Not a circuit address: {address}");
        }

        Multiaddress relayAddress = Multiaddress.Decode(full[..boundary]);
        PeerId destination = address.TerminalP2P() is { } terminal
            ? new PeerId(terminal.ToString())
            : throw new ArgumentException($"The circuit address names no destination peer: {address}");
        return (relayAddress, destination);
    }
}
