// SPDX-FileCopyrightText: 2026 Thomas Suckow
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Ipfs.Status;

/// <summary>
///     Serves the node's status page and its JSON snapshot over
///     plain HTTP (an <see cref="HttpListener"/> — the package
///     takes no web-framework dependency). It is an
///     <see cref="ILibp2pStartup"/>: registered through
///     <c>AddIpfsStatus</c>, it starts with the node.
///
///     The snapshot names every address and peer the node knows,
///     so the default bind is loopback; widen it deliberately.
/// </summary>
public sealed class StatusServer(
    NodeStatusProvider status,
    StatusServerOptions options,
    ILogger<StatusServer>? logger = null) : ILibp2pStartup, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private HttpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _serveLoop;

    /// <summary>The address the page is served at, once started.</summary>
    public string? Address { get; private set; }

    public Task StartAsync(CancellationToken token = default)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{options.BindAddress}:{options.Port}/");
        _listener.Start();
        Address = $"http://{options.BindAddress}:{options.Port}/";
        _stop = new CancellationTokenSource();
        _serveLoop = Task.Run(() => ServeLoopAsync(_stop.Token));
        logger?.LogInformation("Status page at {address}", Address);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stop?.Cancel();
        _listener?.Stop();
        if (_serveLoop is not null)
        {
            try
            {
                await _serveLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listener?.Close();
        _listener = null;
    }

    private async Task ServeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                break; // listener stopped
            }

            _ = Task.Run(() => Handle(context), token);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            (int code, string contentType, string body) = context.Request.Url?.AbsolutePath switch
            {
                "/" or "/index.html" => (200, "text/html; charset=utf-8", StatusPage.Html),
                "/status.json" => (200, "application/json",
                    JsonSerializer.Serialize(status.Build(), JsonOptions)),
                _ => (404, "text/plain; charset=utf-8", "Not found"),
            };

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = code;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception e)
        {
            logger?.LogDebug("Status request failed: {error}", e.Message);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // The client went away; nothing to salvage.
            }
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _listener?.Close();
        _stop?.Dispose();
    }
}

/// <summary>Where the status server binds. Loopback by default — the snapshot is the node's whole address book.</summary>
public sealed class StatusServerOptions
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8190;
}
