# Quiche transport for libp2p

A libp2p transport over Cloudflare quiche, via the [Quiche.NET](https://github.com/tsuckow2/quiche-dotnet) managed API. It exists for one reason msquic cannot serve: quiche owns the application's UDP socket, so one socket can listen and dial at once with connection-ID demux — the prerequisite for DCUtR hole punching from the advertised listen port.

## Status

- Phase 1 (standalone `ITransport` path): dial/listen/echo raw pipe, ephemeral identity, any certificate accepted — the same contract as `QuicProtocol`'s standalone methods and `IpTcpProtocol`'s.
- Phase 2 (shared-port dial through `ITransport`): listen on port P, dial from port P through the transport API.
- Phase 3 (identity): dial-side certificate proof publishing `ProvenPeerId`; inbound proof is a signed `QuicheProofFrame` on a dedicated first stream (see below), NOT a relay-exchanged nonce.
- Phase 4 (stack path): `IProtocol` in the default stack, quiche as the default `quic-v1`, live Forge↔dell gate.

## Inbound identity: why a signed frame, not a punch nonce

The plan's recommendation was a one-time nonce exchanged via relay coordination. Implementing that needs a relay exchange with no clean hook: the DCUtR wire messages are spec-fixed (a new field is a spec deviation), and a separate nonce protocol over the relayed connection needs a shared registry plus DI plumbing from the DCUtR service (transport-agnostic) into this transport. The signed frame achieves the same goal with no new protocol and no DCUtR change: the dialer signs (listener PeerId + dialer PeerId + timestamp + dialer public key) with its identity key and sends it as the first stream's first frame. It is sound because the frame only ever travels inside the TLS-encrypted direct connection to a listener the dialer just authenticated via dial-side certificate proof — an attacker cannot observe it, cannot forge the dialer's signature, and cannot replay it to another listener (bound to the listener's PeerId) or later (120 s window). A dialer that sends no proof frame (any other QUIC implementation) is handed up unproven: the listener peeks at the first stream's first bytes and replays them when they are not the proof magic, so this costs no interop. A confirmed proof frame that fails validation closes the connection; an unproven connection is named by Identify as before. The nonce registry remains a contained future swap: the accept path's first-frame validation is the same shape either way.

## Layout and the Quiche.NET dependency

Quiche.NET is not published to NuGet yet (its step 5). Until then this project builds against a checkout of [tsuckow2/quiche-dotnet](https://github.com/tsuckow2/quiche-dotnet) at the repository root:

```
dotnet-libp2p/
  quiche-dotnet/          # clone of tsuckow2/quiche-dotnet, branch cert-gaps (or later)
  src/libp2p/...
```

The `.csproj` files default `QuicheNetDir` to that location; override with `/p:QuicheNetDir=/path/to/quiche-dotnet/` for other layouts. The quiche-dotnet checkout is untracked (see the root `.gitignore`); it is not a submodule, so nothing pins it — keep it at the branch the transport was last verified against.

The native library comes from the checkout too: run `bash native/build.sh linux-x64` (or your RID) in the quiche-dotnet checkout before `dotnet test`. The test project stages `native/out/` into `runtimes/<rid>/native/` next to the test binary, exactly as Quiche.NET's own tests do; with no native build present the tests fail with `DllNotFoundException` naming `quiche`, which is the signal to build it. When Quiche.NET is published, the `ProjectReference` becomes a `PackageReference` and this section goes away.

## Design notes

- **ALPN is `libp2p`**, as in the msquic transport — not Quiche.NET's `quiche-dotnet` default — so quiche peers interoperate with other libp2p QUIC implementations.
- **One transport instance, shared socket.** The listen socket created by `ListenAsync` serves later dials too, so the source port is the advertised listen port. A dial made before any listen gets its own ephemeral socket; a later listen still creates its own listen socket. Listen-first is the normal node order and the only order the shared-port guarantee covers.
- **Ephemeral identity, temp-file certificates.** quiche's C API loads server certificates from PEM files only (no in-memory loader at 0.30.0), so the transport writes its ephemeral identity's certificate and key to temp files under one directory per transport instance, deleted on dispose.
- **IPv4 only**, like the Quiche.NET socket underneath.
- **Dial timeout is 10 s.** The standalone `DialAsync` has no caller token, so the transport races the handshake against its own timeout; a dead port surfaces as `OperationCanceledException`.
