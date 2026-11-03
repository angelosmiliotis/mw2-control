# User final test — release candidate

Live game behavior, Npcap permission behavior and actual firewall mutations are deliberately NOT verified by development tests. Synthetic tests do not establish anti-cheat compatibility. A private lobby can still be cheat-protected; the checkbox is an acknowledgement, not a technical safety check. Use memory-writing features only where these modifications are permitted, never assume private means VAC-safe.

## Before starting

Close Lindra and the friend's unlocker. Do not run two tuners against the same process. Start the release-candidate EXE from its complete folder (keep companion DLL/runtimeconfig files). No installation or Defender exclusion is required. No automatic elevation, capture, game write or firewall change should happen on startup.

## Read-only first

1. Without MW2 running, Scan should say Game not running; game Apply/Restore stays disabled.
2. With MW2 running, Scan should show exact executable path, fingerprint, observed FOV/FPS and per-feature capability. Unsupported or unreadable features must remain disabled with a reason.
3. Changing a slider/check box alone must NOT change the game. The private/offline acknowledgement resets on manual scan/reset.

## Explicit settings tests (only in an allowed environment)

Test FOV and FPS first; use small changes, verify requested versus observed values, then Restore. 0 FPS means explicit uncapped, not measured zero performance. After Apply, delayed read-only inspections over about two seconds report divergence instead of implying lasting effects. A manual retry can accept a return to the exact captured baseline; unrelated values still fail closed.

If values reset, explicitly Start Keep applied after applying the desired settings. This defaults to stopped and checks at a bounded interval (100ms plus operation time), maintaining only owned targets that return to their exact captured baseline in the same mapping/process. It does not seed new targets, bypass engine clamps, change flags/default fields or guarantee effects between ticks. Another writer's exact baseline is indistinguishable from an engine reset and may be overwritten while this option is active. Any unexpected third value, changed mapping/session or error stops maintenance. Stop/Scan/Reset/Restore/close and revoking acknowledgement stop it; stopping does not restore. For manual Restore, owned-byte checks remain strict: a reset to the baseline may cause refusal rather than a blind write.

Respawn/map changes may replace mappings. If guards refuse, Stop, then Reset baseline (forgets restore history without writing), inspect/reacknowledge and explicitly Apply/Start again. Do not run other tuners at the same time.

Test each advanced control separately and Restore advanced after each. Stream mute affects streamed sounds, not a guaranteed music-only filter. Some changes can require a map rejoin to affect sounds already playing; no snd_restart is run. Detail overlays off is not a guarantee of every weapon camo disappearing. Ambient/S&D selective filtering remains unavailable until a reliable mapping is established; do not expect those unsupported controls to work.

## Lobby Network

Select the active network adapter, explicitly Start, and join/watch a lobby. Capture is IPv4 UDP metadata attributed by current game-owned local endpoints, not an authoritative player list. IPv6, Steam-owned transport and shared relays can hide peers. Check that irrelevant/stale peers disappear after the rolling window, Stop stops updates and Clear resets the visible sample history.

Traffic leader is an UNVERIFIED host estimate. Don't block an address solely because it is first in the table. Blocking a relay/backend/shared host can affect multiple lobbies or connectivity. It does not blacklist a lobby ID and cannot follow an IP change.

## Firewall test (user action only)

Block requires administrator permission. If denied, close and reopen the tool as administrator yourself only if you wish to make the rule; the app must not silently elevate. Select an exact observed public IP, inspect the confirmation's IP and executable path, and Cancel once to verify cancellation is a no-op. Only then confirm a desired block. It may disconnect the current session immediately.

Verify the owned-block list and use Unblock to remove that app-owned pair. No global firewall setting, allow rule or unrelated rule should be changed. A relayed connection may not be blocked by a rule scoped to iw4mp.exe if another executable owns the actual transport; report that limitation rather than widening rules to Steam/all applications.

For any failure, capture the app's diagnostic/status text and say which action, map/mode and audio cue were involved. Do not publish peer IPs or full memory dumps publicly.
