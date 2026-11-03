# Preview 0.4 verification — 2026-11-03

The Windows x64 publish succeeded under `out/preview-0.4`. All 109 core, advanced, and network cases passed (25, 57, and 27 respectively). The fake-memory WPF view-model scenario passed 12 assertions. Coverage includes numeric scoreboard ping, combined Apply and Reset, and retention after Apply.

The published app launched and the Settings and Lobby Network layouts were inspected. In-match rendering, live lobby capture and firewall changes remain for the user's test.

## Historical repair preview 0.3

## Current integrated result

Release build: **0 warnings, 0 errors**. Core runner **25/25**, expanded advanced/maintenance runner **52/52**, network runner **27/27**: **104 synthetic cases, no failures**. The headless WPF view-model runner also passes its lifecycle scenario with **16 assertions**, without opening windows or native process access. Commands are the corresponding `dotnet run --project tests/<name>/<name>.csproj -c Release`, with `--no-launch-profile` for MW2Control.App.Tests, and the Release App build below.

The view-model checks cover default stopped/no writes, missing ledger/confirmation, delayed reset detection without writes, explicit maintenance, frozen inputs, active/revocable acknowledgement, baseline reapply, Stop, manual Scan, Restore, revocation and disposal. Initial test fixture errors (reset injected after delayed verification finished, missing newly required type/domain metadata) were corrected; the complete final run passes. No test failures are being omitted from this report.

Coordinator used targeted **read-only** native inspection and app Diagnostics for the user's reported live failure. Current FOV65/FPS85/stream0 and unchanged fingerprint were observed. There were no coordinator live Apply/Restore/MaintainOwned calls, no capture and no real firewall changes. The user's earlier app history showed immediate write/readback success followed by reversion; that is diagnostic evidence, not successful live verification of the repair. See `LIVE-REPAIR-2026-11-03.md`.

The new package is published separately to `out/repair-0.3`. Native retention, rendered FOV, visual/audio effects and map transitions remain for the user's test. Keep applied does not change declared domains or guarantee persistence between ticks. FOV above the declared80-degree maximum remains experimental.

Published0.3 startup was inspected with Computer Use while the game remained open: NOT SCANNED, Keep applied stopped, and Start/Apply disabled. Neither Scan nor any live write was invoked. Muted text styling was corrected to inherit wrapping so longer explanations fit the cards. The smoke-test window was closed; the user's older running instance was preserved.

## Historical 0.2 verification — 2026-11-02

## Result

Coordinator integration build succeeded with **0 warnings and 0 errors** using .NET SDK 8.0.422. Windows x64 publish succeeded to `out/release-candidate`.

| Runner | Passed | Failed |
| --- | ---: | ---: |
| MW2Control.Tests | 25 | 0 |
| MW2Control.Advanced.Tests | 30 | 0 |
| MW2Control.Network.Tests | 27 | 0 |
| Total | **82** | **0** |

Run command:

```powershell
dotnet run --project tests/MW2Control.Tests/MW2Control.Tests.csproj
dotnet run --project tests/MW2Control.Advanced.Tests/MW2Control.Advanced.Tests.csproj -c Release
dotnet run --project tests/MW2Control.Network.Tests/MW2Control.Network.Tests.csproj -c Release
```

Counts are from the final integrated source. The first runner also ran in Release. Build command: `dotnet build src/MW2Control.App/MW2Control.App.csproj -c Release --nologo`. Publish command: `scripts/Build-Preview.ps1 -Flavor release-candidate`.

## Coverage in the runner

- FOV/FPS bounds, inclusive endpoints, NaN and infinities
- Hash, architecture, process name and executable path checks
- Null, misaligned and out-of-range pointers; expected and wrong dvar names
- Invalid/stale identity, short reads, guarded and uncommitted regions
- Read-only and executable target protections, and read-only inspection
- Apply permission gate, partial-write rollback, readback mismatch, restore ownership and session identity
- `ControlEngine` inspection with no writes, unknown-build apply rejection, and missing-target state

All process memory is synthetic through `IProcessMemory` and a fake `IProcessSource`; no game process is opened or written. This is mock-adapter coverage only. No live-game or in-game verification was performed.

## Stage 2 coverage

Advanced tests cover exact byte widths, optional missing capabilities, compound preflight, validated enum tables/domains, ownership-aware partial rollback, process transitions, independent restore ledgers, explicit uncapped FPS and read-only ResetSession/disposal.

Network tests cover malformed/truncated/VLAN/raw/loopback packets, ignored fragments/unsupported traffic, local endpoint attribution, bounded rolling counters, fake capture lifetime and endpoint changes, exact address/path validation, confirmation gates, idempotent firewall pairs, partial rollback, network-change handling and conflicting-rule rejection. They never capture live traffic or instantiate mutating COM.

Published app launched. Coordinator inspected Overview, Audio Lab, Visuals and Lobby Network with Computer Use. Requested defaults and disabled write controls were visible; capture was stopped, peers empty, Block/Unblock disabled. No Scan, Apply, capture Start or real firewall mutation was executed. After final republish/relaunch, the release-candidate footer, dark adapter ComboBox and minimum-height empty peer panel were visually verified. One UI automation attempt had unavailable geometry; re-observation resolved it. Full keyboard/DPI and native capture behavior remain unverified.

## Remaining live-test limits

Actual game effects/map transitions, native capture access and real firewall behavior remain for the user's final test. No VAC compatibility is established; private confirmation does not prove an unprotected session. Selective ambient gunfire/S&D filtering is unresolved, stream mute is partial, and detail suppression does not guarantee full No Camos. IPv4 metadata and traffic leader do not establish all players or proven host. Relays/Steam-owned transport can hide attribution or bypass iw4mp.exe-scoped blocking. Follow `docs/FINAL-TEST-GUIDE.md`.
