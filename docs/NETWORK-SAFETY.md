# Lobby Network acceptance and limits

The user requested observed lobby IPs and reversible self-side blocking to avoid reconnecting to a host. This is not a kick tool and must not send traffic, alter the remote host or attack a lobby.

## Observations are not player identity

Windows UDP owner tables show local bound endpoints and PID, not connected remote players. Passive packet metadata is needed to observe remote IPs. Only traffic matching the game's current local address/UDP ports is in scope. A busy IP may be a peer, backend or Steam relay. The traffic leader is a heuristic and must never be labelled a verified host or mapped to a Steam name without separate evidence. Blocking a shared relay may exclude more than one lobby; host IP changes evade an old block naturally.

## Explicit action boundaries

- No capture at startup or after loading a saved profile.
- No payload retention, PCAP export, geolocation API, DNS lookups or external uploads.
- No capture-driver installation/bundling/startup changes; use already-installed trusted Npcap only.
- No automatic block of the traffic leader. Show exact selected IP and executable path in confirmation.
- No wildcard/CIDR/subnet, gateway/local/private/self, multicast, limited-broadcast or unspecified target blocks. A remote host ending in .0 or .255 is not automatically a broadcast/network address without its subnet mask. Validate actual special ranges instead of guessing a /24.
- Block rules are exact IP + game executable + UDP, in app-owned group, inbound/outbound only. Do not modify global firewall defaults, profile states or unrelated rules.
- Cancellation is a no-op. If administrative permission is missing, show an error; no silent privilege escalation.
- Unblock removes only validated application-owned entries. A duplicate/preexisting rule must not be erased during rollback of another operation. Previously owned rules remain removable if a later network change makes their address local; locality rejection applies when adding a block, not when removing an owned one.

## Development verification

Packet parsing, aggregation, endpoint attribution and firewall transactions must be tested with synthetic data and fake policy/capture adapters. Do not capture live traffic or create/delete real firewall rules as part of development smoke tests. Real driver permission behavior, Steam relay visibility, in-lobby attribution and successful block/unblock remain user final-test items.
