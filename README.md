# MW2 CONTROL — preview 0.4.3

Native Windows x64 companion for MW2 (2009). An independent implementation, not a repack of Lindra or another unlocker.

## What this preview is for

- A real WPF interface with manual, read-only process inspection.
- FOV/FPS requested vs observed values and explicit per-build validation. Startup requests default to 90° and 333 FPS.
- One Settings tab with Apply and Reset for the validated game session.
- Streamed-audio mute, fullbright, fog/glow toggles, specular modes and compound detail-overlay suppression, validated independently for the exact supported build.
- Explicit uncapped FPS (0) and Numeric ping for scoreboard latency.
- Apply starts retention for owned targets that match the last write or the exact captured baseline in the same process and mapping. Unexpected values stop it. Reset stops retention and restores captured originals where ownership checks pass.
- Lobby Network groups Scan lobby and Block likely host, with adapter selection and reversible owned blocks available separately.
- Confirmed exact-public-IP UDP firewall blocks scoped to iw4mp.exe, with owned-rule listing/removal. No automatic elevation.

Apply authorizes writes and retention for the current game lifetime. Apply again for a replacement process. Diagnostics shows the last observed values and operation history. Firewall operations may require administrator permission.

Stream mute is partial: selective off-map gunfire/S&D cue filtering remains unresolved. Detail overlays do not guarantee complete No Camos. Values may reset at map/respawn transitions; maintenance does not follow a changed mapping or bypass engine clamps, and cannot guarantee effects between ticks. It can overwrite an exact baseline written by the game or another writer while explicitly active. Traffic volume cannot prove host identity; relays, Steam-owned transport, IPv6 and changed IPs can hide peers or defeat an executable-scoped block. Blocking an IP does not blacklist a lobby ID.

Use the controls where game modifications are permitted. Close other tuners before live testing to avoid competing writes.

## Build

Requires .NET 8 SDK and Windows for the WPF application.

```powershell
dotnet build src/MW2Control.App/MW2Control.App.csproj -c Release
dotnet run --project tests/MW2Control.Tests/MW2Control.Tests.csproj -c Release
dotnet run --project tests/MW2Control.Advanced.Tests/MW2Control.Advanced.Tests.csproj -c Release
dotnet run --project tests/MW2Control.Network.Tests/MW2Control.Network.Tests.csproj -c Release
dotnet run --project tests/MW2Control.App.Tests/MW2Control.App.Tests.csproj -c Release --no-launch-profile
pwsh -File scripts/Build-Preview.ps1
```

The last command publishes a framework-dependent preview under `out/preview-0.4.3`. Run `MW2Control.exe` from that folder and keep its files together. The app uses the Windows Desktop .NET 8 runtime, and lobby observation uses the installed Npcap driver.

See `docs/FINAL-TEST-GUIDE.md` for the staged live test and `docs/AUDIO-FINDINGS.md` for unresolved audio work. Synthetic results are in `docs/TEST-REPORT.md`; they are not a live compatibility or anti-cheat guarantee.

## Layout

- `src/MW2Control.Core` — profile identification, guarded external access, transactions.
- `src/MW2Control.App` — WPF interface and presentation state.
- `src/MW2Control.Network` — passive metadata observation and scoped owned firewall blocks.
- `tests/MW2Control.Tests` — synthetic tests, no real game access.
- `docs/BUILD-BRIEF.md` — design/safety contract and model allocation.
- `docs/ADMIN-STATUS.md` — coordinating checklist.
- `docs/TEST-REPORT.md` — actual test results and limitations, when generated.
