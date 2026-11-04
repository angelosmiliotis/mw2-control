# Lobby Network

This module reads game-owned UDP endpoints, captures recent IPv4 traffic through Npcap and manages firewall blocks created by MW2 Control.

Capture begins when the user starts a scan. The reader closes its own capture handle when stopped. The game's process, start time, path and UDP ports are checked once per second. Endpoint changes update the filter and clear the previous sample.

## Traffic

The parser supports Ethernet with up to two VLAN tags, Windows loopback, DLT_LOOP, RAW and IPv4 link types. IPv6 capture and fragmented packets are excluded.

The app keeps a rolling 15-second window, capped at 256 peers and 4096 metadata samples per peer. Rates can be undercounted during heavy traffic. Packet payloads are read temporarily for parsing and are not retained.

The busiest connection is used as a likely-host estimate. Shared relays can hide the actual host.

## Firewall rules

Each block has inbound and outbound UDP rules for an exact public IP and `iw4mp.exe`. Rule names, group and scope are checked before listing or removing a block. Duplicate or conflicting rules are rejected. Partial creation failures roll back newly added rules where possible.

Administrator access is required to add or remove rules. Existing unrelated rules and global firewall settings are preserved.

## Tests

```powershell
dotnet run --project tests/MW2Control.Network.Tests/MW2Control.Network.Tests.csproj -c Release
```

The 27 cases cover parsing, filtering, rolling counters, capture lifecycle, session changes, address validation and firewall-rule handling. They use test adapters.

References used for the implementation include the [Npcap developer guide](https://npcap.com/guide/npcap-devguide.html), [pcap_next_ex](https://npcap.com/guide/wpcap/pcap_next_ex.html), the [Windows UDP owner table](https://learn.microsoft.com/en-us/windows/win32/api/udpmib/ns-udpmib-mib_udprow_owner_pid) and [Windows Firewall rule API](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nn-netfw-inetfwrule).
