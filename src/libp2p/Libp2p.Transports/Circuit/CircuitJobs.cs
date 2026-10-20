// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Circuit;

/// <summary>
///     The rendezvous between the circuit transport and the hop/stop
///     protocol instances the stack creates per stream — the same
///     claim-a-queued-job pattern the Kad client uses. The transport
///     knows WHAT it wants (a reservation, a connection to a named
///     destination, an inbound stream's owner); the protocol instance
///     that the relay connection's stack dispatches only knows WHICH
///     connection it is running on. Jobs are queued against the peer
///     at the other end of that connection and claimed by its id.
///     One instance per node scope (registered in DI by AddLibp2p).
/// </summary>
public sealed class CircuitJobs
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<HopJob>> _hopJobs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hopGates = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<StopJob>> _stopJobs = new();
    private readonly ConcurrentDictionary<string, Func<StopAcceptance, Task>> _listeners = new();

    /// <summary>
    ///     Serializes hop requests per relay: jobs are claimed in
    ///     queue order, so the enqueue-and-dial sequence for one relay
    ///     must not interleave with another's, or a claim could take
    ///     the wrong destination.
    /// </summary>
    public SemaphoreSlim HopGate(PeerId relay) => _hopGates.GetOrAdd(relay.ToString(), _ => new SemaphoreSlim(1, 1));

    public void EnqueueHop(PeerId relay, HopJob job) =>
        _hopJobs.GetOrAdd(relay.ToString(), _ => new ConcurrentQueue<HopJob>()).Enqueue(job);

    public HopJob? ClaimHop(PeerId relay) =>
        _hopJobs.TryGetValue(relay.ToString(), out ConcurrentQueue<HopJob>? queue) && queue.TryDequeue(out HopJob? job)
            ? job
            : null;

    public void EnqueueStop(PeerId destination, StopJob job) =>
        _stopJobs.GetOrAdd(destination.ToString(), _ => new ConcurrentQueue<StopJob>()).Enqueue(job);

    public StopJob? ClaimStop(PeerId destination) =>
        _stopJobs.TryGetValue(destination.ToString(), out ConcurrentQueue<StopJob>? queue) && queue.TryDequeue(out StopJob? job)
            ? job
            : null;

    /// <summary>
    ///     Registers the sink a circuit listener accepts inbound
    ///     (stop) streams through, keyed by the listening peer.
    /// </summary>
    public void RegisterListener(PeerId localPeer, Func<StopAcceptance, Task> sink) =>
        _listeners[localPeer.ToString()] = sink;

    public Func<StopAcceptance, Task>? ListenerFor(PeerId localPeer) =>
        _listeners.TryGetValue(localPeer.ToString(), out Func<StopAcceptance, Task>? sink) ? sink : null;

    public void UnregisterListener(PeerId localPeer) =>
        _listeners.TryRemove(localPeer.ToString(), out _);
}

/// <summary>
///     One hop request the transport wants performed on a relay
///     connection: a reservation, or a connection to a destination.
///     The hop protocol instance completes <see cref="Established"/>
///     when the relay answers, then either pumps the stream against
///     the channel the transport supplies through
///     <see cref="PumpTo"/> (a switch-stack dial), or hands the raw
///     stream over through <see cref="Stream"/> and waits for
///     <see cref="Closed"/> (a raw transport dial, or a reservation
///     being held open).
/// </summary>
public sealed class HopJob
{
    private HopJob()
    {
    }

    public static HopJob Reserve() => new() { IsReserve = true };

    public static HopJob Connect(PeerId destination, bool raw = false) => new() { Destination = destination, Raw = raw };

    public bool IsReserve { get; private init; }
    public PeerId? Destination { get; private init; }
    public bool Raw { get; private init; }

    /// <summary>The relay connection the job was claimed on.</summary>
    public IRemotePeerConnection? RelayConnection { get; set; }

    public long ReservationExpireUnix { get; set; }

    public TaskCompletionSource Established { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<IChannel> PumpTo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<IChannel> Stream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
///     One stop request a relay wants performed toward a destination:
///     open a stop stream carrying a connection from
///     <see cref="Source"/>, then pump it against the hop stream the
///     relay already holds (<see cref="UpChannel"/>).
/// </summary>
public sealed class StopJob
{
    public required PeerId Source { get; init; }
    public required IChannel UpChannel { get; init; }
    public TaskCompletionSource Established { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
///     An inbound relayed stream offered to a circuit listener: the
///     raw stream, the peer the relay says it comes from (the
///     security layer above will prove or refute that), and the
///     circuit address that source is reachable at.
/// </summary>
public sealed record StopAcceptance(IChannel Channel, PeerId Source, Multiaddress SourceAddress);
