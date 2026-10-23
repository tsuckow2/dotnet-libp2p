# DCUtR protocol

Direct Connection Upgrade through Relay: hole punching synchronized
over an existing relayed (circuit) connection, so two peers that can
only reach each other through a relay can upgrade to a direct
connection — QUIC first, as UDP simultaneous open is the reliable
punch. If the upgrade fails, the relayed connection stays in use.

- [libp2p spec](https://github.com/libp2p/specs/blob/master/relay/DCUtR.md)
