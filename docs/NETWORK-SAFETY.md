# How Lobby Network works

Scan lobby watches IPv4 UDP traffic associated with the running game's local ports. It counts recent traffic and shows the busiest connection as the likely host.

An IP address alone cannot reliably identify a Steam player. Shared relays and Steam-owned transport can hide the actual host. The counters use a rolling 15-second window, so the result can change as traffic changes.

## Blocking an address

A block adds inbound and outbound UDP firewall rules for that exact IP and the game's executable. The app requests administrator access for this operation when needed. A separate helper receives one validated block or unblock request over a local pipe and exits when finished. Blocking may disconnect the current lobby or affect other lobbies sharing the same IP.

Unblock removes rules created by MW2 Control after checking their scope. The app preserves unrelated rules. Addresses remain blocked until you remove their rules. A host that changes IP may become reachable again.

## Data handling

Capture starts when Scan lobby is selected. The app keeps recent IP metadata and traffic counters in memory. Packet payloads, player profiles and geolocation lookups are outside this feature.

The implementation and its test command are described in the [network module](../src/MW2Control.Network/README.md).
