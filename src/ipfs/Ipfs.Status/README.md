# Nethermind.Ipfs.Status

A live status page for a libp2p/IPFS node: the local peer's
advertised addresses with their per-address evidence, the
switch's live connections, every known peer's address book, and
the Kad DHT state (known peers and stored provider records).

Register it with the rest of the stack and it starts with the
node (it is an `ILibp2pStartup`):

```csharp
services
    .AddKadDht()
    .AddLibp2pLocalPeer(localPeer)
    .AddLibp2p(...)
    .AddIpfsStatus();          // http://127.0.0.1:8190/
```

The page polls `/status.json` every two seconds. Bind address
and port are configurable through `AddIpfsStatus(options => ...)`;
the default binds loopback only — the snapshot names every
address and peer the node knows, so expose it deliberately.
