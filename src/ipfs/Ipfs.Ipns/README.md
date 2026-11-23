# Nethermind.Ipfs.Ipns

IPNS records over the Kad DHT: one stable name pointing at a changing
value (a manifest CID), signed and sequenced by the name's own key.

## The record

A canonical dag-cbor map, byte-identical to the backup prototype's
record format (`BackupPrototype.Config/IpnsRecord.cs` — same fields,
same signature input, same resolver rules):

| field | type | meaning |
|---|---|---|
| `name` | text | PeerID of the IPNS key (`12D3KooW…`) |
| `value` | text | current manifest CID (`bafyrei…`) |
| `sequence` | int | monotonic, first = 1, +1 per publish |
| `validity` | text | RFC 3339 UTC expiry (24 h from publish) |
| `validity_type` | text | always `"eol"` |
| `ttl_seconds` | int | cache TTL (3600) |
| `public_key` | bytes | 32-byte Ed25519 key the name derives from |
| `signature` | bytes | 64-byte Ed25519 over the map without `signature` |

The DHT key is ASCII `"/ipns/"` + the binary PeerID. The record is
self-authenticating: a resolver verifies the signature against the
embedded key, checks the key's PeerID equals the name, and takes the
strictly newest sequence. The announcer's identity is irrelevant — a
delegate may publish on the owner's behalf; the record's own signature
is the authority.

## Using it

```csharp
services
    .AddKadDht()
    .AddIpfsIpns(ipnsKey); // a dedicated Ed25519 Identity, never the node identity

IpnsService ipns = services.GetRequiredService<IpnsService>();
IpnsRecord published = await ipns.PublishAsync(manifestCid);
(IpnsRecord Record, string Source)? found = await ipns.ResolveAsync(name);
```

`AddIpfsIpns` also registers the `"/ipns/"` value validator, so the
node stores and serves IPNS records for other names. Publishing is
best-effort at the DHT level (announces are per-peer best-effort, as
with providing); a republish loop re-announces the held record every
12 h so the name stays resolvable.

## What this is not (yet)

The value on the wire is the prototype's dag-cbor record. Kubo/go
nodes validate `/ipns/` values as IpnsEntry protobuf, so our records
do not replicate on the public DHT — the proof is a closed DHT of
fork nodes. Speaking IpnsEntry (a second signature over the protobuf
serialization, translated at the edge) is the named follow-up if
public-network replication is wanted; the Kad plumbing is
codec-agnostic and needs no change for it.
