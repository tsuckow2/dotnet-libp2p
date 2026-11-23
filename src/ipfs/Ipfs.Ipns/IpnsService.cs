// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Ipfs.Kad;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Ipns;

/// <summary>How an <see cref="IpnsService"/> publishes and republishes.</summary>
public sealed class IpnsOptions
{
    /// <summary>
    ///     How often the service re-announces its own held record to
    ///     the DHT. A record's validity is 24 h; re-putting at half
    ///     that keeps the name resolvable across publisher restarts
    ///     without waiting for a new manifest to trigger a publish.
    /// </summary>
    public TimeSpan RepublishInterval { get; set; } = TimeSpan.FromHours(12);
}

/// <summary>
///     Publishes and resolves IPNS records over the DHT, for one
///     name: the PeerID of the dedicated IPNS key this service was
///     built with (never the node identity key — one key, one job).
///     Publishing signs a fresh record (sequence = last + 1, fresh
///     24 h validity) and announces it with
///     <see cref="KadClient.PutValueAsync"/>; resolving walks the
///     DHT for the name's key and accepts the best valid record it
///     finds. A republish loop re-announces the held record so the
///     name stays resolvable while nothing new is published.
///     The service never trusts a record it has not verified itself:
///     every candidate passes the resolver's Verify + sequence
///     rules before it is held or returned.
/// </summary>
public sealed class IpnsService : ILibp2pStartup, IDisposable
{
    private readonly Identity _ipnsKey;
    private readonly KadClient _kad;
    private readonly IpnsOptions _options;
    private readonly ILogger? _logger;
    private readonly IpnsResolver _resolver = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _loopGate = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    public IpnsService(Identity ipnsKey, KadClient kad, IpnsOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        _ipnsKey = ipnsKey;
        _kad = kad;
        _options = options ?? new IpnsOptions();
        _logger = loggerFactory?.CreateLogger<IpnsService>();
    }

    /// <summary>The name this service publishes: the PeerID of its IPNS key.</summary>
    public string Name => _ipnsKey.PeerId.ToString();

    /// <summary>
    ///     Publishes a new record for the current value: sequence =
    ///     last + 1 (or 1), fresh 24 h validity, signed by the IPNS
    ///     key. The record is verified the way any resolver would,
    ///     and accepted locally, before it leaves this node.
    ///     Publishing is best-effort at the DHT level (announces are
    ///     per-peer best-effort, as with providing) — the returned
    ///     record is the published truth either way, and the
    ///     republish loop re-announces it.
    /// </summary>
    public async Task<IpnsRecord> PublishAsync(string value, CancellationToken token = default)
    {
        await _publishGate.WaitAsync(token);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            long sequence = (_resolver.Get(Name)?.Record.Sequence ?? 0) + 1;
            var record = new IpnsRecord
            {
                Name = Name,
                Value = value,
                Sequence = sequence,
                Validity = now.Add(IpnsRecord.DefaultValidity),
                ValidityType = IpnsRecord.ValidityTypeEol,
                TtlSeconds = IpnsRecord.DefaultTtlSeconds,
                PublicKey = _ipnsKey.PublicKey.Data.ToByteArray(),
            };
            record.Sign(_ipnsKey);
            var (ok, reason) = record.Verify(now);
            if (!ok) throw new InvalidOperationException($"Freshly signed IPNS record does not verify: {reason}");
            var (outcome, acceptReason) = _resolver.TryAccept(record, now);
            if (outcome is not (IpnsAcceptOutcome.Accepted or IpnsAcceptOutcome.Duplicate))
                throw new InvalidOperationException($"Freshly signed IPNS record was not accepted locally: {acceptReason}");

            await _kad.PutValueAsync(IpnsRecord.DhtKey(Name), record.Encode(), token);
            _logger?.LogInformation("IPNS record published: name={Name} sequence={Sequence} value={Value} validity={Validity}",
                Name, sequence, value, record.Validity);
            return record;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    ///     Resolves a name: the held record when one is known
    ///     ("local" for this service's own name, "dht" for a record
    ///     learned from the network), otherwise a DHT walk for the
    ///     name's key. Null = never seen. A record learned from the
    ///     DHT is verified before it is held or returned.
    /// </summary>
    public async Task<(IpnsRecord Record, string Source)?> ResolveAsync(string name, CancellationToken token = default)
    {
        var held = _resolver.Get(name);
        if (held is not null)
        {
            return (held.Value.Record, name == Name ? "local" : "dht");
        }

        IReadOnlyList<byte[]> candidates = await _kad.GetValueAsync(IpnsRecord.DhtKey(name), token);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (byte[] candidate in candidates)
        {
            IpnsRecord record;
            try
            {
                record = IpnsRecord.Decode(candidate);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("IPNS candidate for {name} does not decode ({error}); dropped", name, ex.Message);
                continue;
            }

            if (record.Name != name)
            {
                _logger?.LogDebug("IPNS candidate for {name} names {other} instead; dropped", name, record.Name);
                continue;
            }

            var (outcome, reason) = _resolver.TryAccept(record, now);
            if (outcome is IpnsAcceptOutcome.Rejected or IpnsAcceptOutcome.Stale)
            {
                _logger?.LogDebug("IPNS candidate for {name} not accepted ({reason})", name, reason);
            }
        }

        held = _resolver.Get(name);
        return held is null ? null : (held.Value.Record, "dht");
    }

    /// <summary>Restores a persisted record into the held state (restart recovery — no sequence bump, no announce).</summary>
    public void Seed(IpnsRecord record, DateTimeOffset fetchedAt) => _resolver.SeedHeld(record, fetchedAt);

    /// <summary>Starts the republish loop (begun by StartLibp2pAsync; the loop outlives the startup call).</summary>
    public Task StartAsync(CancellationToken token)
    {
        lock (_loopGate)
        {
            if (_loop is not null) return Task.CompletedTask;
            _loopCts = new CancellationTokenSource();
            _loop = RepublishLoopAsync(_loopCts.Token);
        }

        return Task.CompletedTask;
    }

    private async Task RepublishLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_options.RepublishInterval, token);
                var held = _resolver.Get(Name);
                if (held is { } own && own.Record.Validity > DateTimeOffset.UtcNow)
                {
                    try
                    {
                        await _kad.PutValueAsync(IpnsRecord.DhtKey(Name), own.Record.Encode(), token);
                        _logger?.LogDebug("IPNS record re-published: name={Name} sequence={Sequence}",
                            Name, own.Record.Sequence);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("IPNS republish failed: {error}", ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the loop ends with the service.
        }
    }

    /// <summary>
    ///     Stops the republish loop. Idempotent: the service is
    ///     registered under two service types (IpnsService and
    ///     ILibp2pStartup), and the container disposes it once per
    ///     registration.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? loopCts;
        lock (_loopGate)
        {
            if (_disposed) return;
            _disposed = true;
            loopCts = _loopCts;
            _loopCts = null;
        }

        try
        {
            loopCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop already ended with its own token.
        }

        loopCts?.Dispose();
        _publishGate.Dispose();
    }
}

public static class IpnsServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the IPNS service for one name: the PeerID of
    ///     <paramref name="ipnsKey"/>, a dedicated Ed25519 keypair —
    ///     never the node identity key. Also registers the "/ipns/"
    ///     value validator, so this node stores and serves IPNS
    ///     records for other names too. Requires
    ///     <see cref="KadServiceCollectionExtensions.AddKadDht"/>
    ///     first (the service publishes through the Kad client).
    /// </summary>
    public static IServiceCollection AddIpfsIpns(this IServiceCollection services, Identity ipnsKey, Action<IpnsOptions>? configure = null)
    {
        var options = new IpnsOptions();
        configure?.Invoke(options);
        services
            .AddSingleton(options)
            .AddSingleton<IpnsService>(provider => new IpnsService(
                ipnsKey,
                provider.GetRequiredService<KadClient>(),
                provider.GetRequiredService<IpnsOptions>(),
                provider.GetService<ILoggerFactory>()))
            .AddSingleton<IDhtValueValidator, IpnsValueValidator>()
            .AddSingleton<ILibp2pStartup>(provider => provider.GetRequiredService<IpnsService>());
        return services;
    }
}
