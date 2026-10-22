// SPDX-FileCopyrightText: 2026 Thomas Suckow
// SPDX-License-Identifier: MIT

namespace Nethermind.Ipfs.Status;

/// <summary>
///     The status page itself: one self-contained document that
///     polls /status.json and renders the snapshot. No external
///     assets — the package serves it as-is.
/// </summary>
internal static class StatusPage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>libp2p node status</title>
        <style>
          body { font-family: ui-monospace, Menlo, Consolas, monospace; margin: 2rem auto; max-width: 1100px; padding: 0 1rem; background: #11151c; color: #d7dde8; }
          h1 { font-size: 1.25rem; } h2 { font-size: 1rem; margin-top: 2rem; color: #9fd0ff; }
          .muted { color: #8a94a6; } .err { color: #ff8080; }
          table { border-collapse: collapse; width: 100%; margin: .5rem 0 1rem; }
          th, td { border-bottom: 1px solid #2a3242; padding: .3rem .5rem; text-align: left; vertical-align: top; font-size: .82rem; }
          th { color: #8a94a6; font-weight: normal; }
          .id { word-break: break-all; } .ok { color: #7ee2a0; } .badge { border: 1px solid #2a3242; border-radius: .6rem; padding: 0 .5rem; }
        </style>
        </head>
        <body>
        <h1>libp2p node status</h1>
        <div id="summary" class="muted">loading…</div>

        <h2>Local peer</h2>
        <div id="local"></div>

        <h2>Connections <span id="conn-count" class="muted"></span></h2>
        <div id="connections"></div>

        <h2>Peers <span id="peer-count" class="muted"></span></h2>
        <div id="peers"></div>

        <h2>DHT <span id="dht-count" class="muted"></span></h2>
        <div id="dht"></div>

        <script>
        const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
        const short = id => id && id.length > 16 ? id.slice(0, 8) + '…' + id.slice(-6) : id;
        const when = t => t ? new Date(t).toLocaleTimeString() : '—';

        function addrRows(addresses) {
          if (!addresses.length) return '<span class="muted">none</span>';
          return '<table><tr><th>address</th><th>sources</th><th>ok</th><th>fail</th><th>vouches</th><th>last success</th><th>last failure</th></tr>' +
            addresses.map(a => `<tr><td class="id">${esc(a.address)}</td><td>${esc(a.sources)}</td><td class="ok">${a.successfulDials}</td><td>${a.failedDials}</td><td>${a.vouchCount}</td><td>${when(a.lastSuccessAt)}</td><td>${when(a.lastFailureAt)}</td></tr>`).join('') + '</table>';
        }

        async function refresh() {
          try {
            const snap = await (await fetch('/status.json')).json();
            document.getElementById('summary').textContent =
              `peer ${snap.local.peerId} · updated ${new Date(snap.at).toLocaleTimeString()} · refreshes every 2s`;
            document.getElementById('local').innerHTML =
              '<div>advertised:</div>' +
              (snap.local.advertisedAddresses.length
                ? '<ul>' + snap.local.advertisedAddresses.map(a => `<li class="id">${esc(a)}</li>`).join('') + '</ul>'
                : '<div class="muted">none yet</div>') + addrRows(snap.local.addresses);

            document.getElementById('conn-count').textContent = `(${snap.connections.length})`;
            document.getElementById('connections').innerHTML = snap.connections.length
              ? '<table><tr><th>remote peer</th><th>proven</th><th>remote endpoint</th><th>local endpoint</th><th>via relay</th><th>since</th></tr>' +
                snap.connections.map(c => `<tr><td class="id" title="${esc(c.remotePeerId)}">${esc(short(c.remotePeerId))}</td><td>${c.provenPeerId ? '<span class="ok">yes</span>' : '—'}</td><td class="id">${esc(c.remoteEndpoint)}</td><td class="id">${esc(c.localEndpoint)}</td><td>${c.viaRelay ? 'yes' : '—'}</td><td>${when(c.establishedAt)}</td></tr>`).join('') + '</table>'
              : '<span class="muted">none</span>';

            document.getElementById('peer-count').textContent = `(${snap.peers.length})`;
            document.getElementById('peers').innerHTML = snap.peers.length
              ? snap.peers.map(p => `<div><span class="id">${esc(p.peerId)}</span> ${p.connected ? '<span class="badge ok">connected</span>' : '<span class="badge muted">known</span></div>` + addrRows(p.addresses)).join('')
              : '<span class="muted">none</span>';

            const dht = document.getElementById('dht');
            if (!snap.dht) {
              document.getElementById('dht-count').textContent = '';
              dht.innerHTML = '<span class="muted">no DHT registered</span>';
            } else {
              document.getElementById('dht-count').textContent =
                `(${snap.dht.knownPeers.length} known, ${snap.dht.providerRecords.length} provider records)`;
              dht.innerHTML =
                snap.dht.providerRecords.map(r => `<div>provides <span class="id">${esc(r.key)}</span> by <span class="id" title="${esc(r.providerPeerId)}">${esc(short(r.providerPeerId))}</span> <span class="muted">until ${when(r.expiresAt)}</span></div>`).join('') +
                '<table><tr><th>peer</th><th>addresses</th></tr>' +
                snap.dht.knownPeers.map(p => `<tr><td class="id" title="${esc(p.peerId)}">${esc(short(p.peerId))}</td><td class="id">${p.addresses.map(esc).join('<br>')}</td></tr>`).join('') + '</table>';
            }
          } catch (e) {
            document.getElementById('summary').innerHTML = '<span class="err">status fetch failed: ' + esc(e.message) + '</span>';
          }
        }
        refresh();
        setInterval(refresh, 2000);
        </script>
        </body>
        </html>
        """;
}
