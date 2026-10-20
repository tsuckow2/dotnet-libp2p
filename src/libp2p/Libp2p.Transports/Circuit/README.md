# Libp2p.Protocols.Circuit

Circuit Relay v2, client side: a transport that serves `/p2p-circuit`
addresses by cutting the address at the circuit boundary, dialing the
relay through the switch, and running the hop CONNECT protocol
(`/libp2p/circuit/relay/0.2.0/hop`) to reach the destination. The stack
above the transport (security, muxer, applications) runs end-to-end
with the destination over the relayed stream — the relay is an
untrusted pipe. Listening reserves on the relay and accepts inbound
connections delivered over the stop protocol
(`/libp2p/circuit/relay/0.2.0/stop`).

The relay *server* role (accepting reservations and splicing streams
for other peers) is not implemented here.
