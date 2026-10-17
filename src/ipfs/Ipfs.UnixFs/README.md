# Nethermind.Ipfs.UnixFs

The UnixFS data model over dag-pb, for .NET: decode a file's DAG
(PBNode links + UnixFS data) and reassemble the file from its
blocks, wherever they were fetched from. No networking here — pair
it with Nethermind.Ipfs.Bitswap to get the blocks.
