# Nethermind.Ipfs.Bitswap

Bitswap 1.1.0 (`/ipfs/bitswap/1.1.0`) for .NET, as a managed-tier
protocol on Nethermind libp2p: negotiate, then exchange protobuf
wantlists and block payloads. `BitswapSession` is the typed comm the
protocol hands back — it answers wants from an `IBlockStore`, and a
received block only completes a want when the sha2-256 multihash
recomputed from its content matches the wanted id.

Block ids are raw sha2-256 multihashes. Files in the sample are a
manifest block over chunk blocks; real IPFS files are UnixFS DAGs,
which live in Nethermind.Ipfs.UnixFs.
