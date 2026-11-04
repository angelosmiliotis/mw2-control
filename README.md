# MW2 Control

A small Windows app for Call of Duty: Modern Warfare 2 (2009) on Steam. Change your FOV and FPS cap, quiet the background audio, and keep your settings in one place.

**[Download 1.1 Standalone](https://github.com/angelosmiliotis/mw2-control/releases/download/v1.1/MW2-Control-1.1-Standalone-x64.exe)** · [Small download](https://github.com/angelosmiliotis/mw2-control/releases/download/v1.1/MW2-Control-1.1-Small-x64.exe) · [Report a bug](https://github.com/angelosmiliotis/mw2-control/issues/new?template=bug-report.yml) · [Suggest something](https://github.com/angelosmiliotis/mw2-control/issues/new?template=feedback.yml)

1.1 is available for testing. The single-file downloads, administrator prompt and in-game transitions still need manual testing. [All releases](https://github.com/angelosmiliotis/mw2-control/releases).

![Settings in MW2 Control](docs/screenshots/settings.png)

## Getting started

1. Download the Standalone EXE for Windows x64.
2. Start MW2 Multiplayer through Steam, then open the downloaded EXE.
3. Pick your settings and click **Apply**.

Your choices are saved when you Apply or close the app. Apply also keeps the settings active and retries recognized resets while the same game process is running. If you restart the game, click Apply again. **Reset** stops this and restores the original values where they can still be verified.

Standalone is a single EXE with the .NET 8 Desktop Runtime included. The optional Small download needs the .NET 8 Desktop Runtime for x64 already installed. Npcap is needed for lobby scanning with either download.

## What you can change

- **FOV and FPS** - starts at 90 FOV and 333 FPS on a fresh install, with an uncapped FPS option.
- **Background audio** - stream mute quiets streamed audio. Footsteps, gunfire, explosions and the bomb timer remained audible in multiplayer testing.
- **Visuals** - fullbright, fog, glow, specular mode and detail overlays.
- **Scoreboard** - show ping in milliseconds.
- **Lobby Network** - find the busiest lobby connection and block or unblock its IP for MW2.

The connection indicator updates automatically. Details about the game build, settings and errors are in **Diagnostics**.

<details>
<summary>More screenshots</summary>

![Diagnostics in MW2 Control](docs/screenshots/diagnostics.png)

![Lobby Network in MW2 Control](docs/screenshots/lobby.png)

</details>

## Lobby Network

Install [Npcap](https://npcap.com/#download), join a lobby, then click **Scan lobby**. The busiest connection appears as the likely host. Steam relays can make that estimate inaccurate.

Block and Unblock request administrator access when needed. Blocking can disconnect you and affect other lobbies sharing the same IP. You can remove a block from the list in the app. The adapter used for a successful scan is remembered.

## A few things to know

The app supports a specific x64 Steam build of `iw4mp.exe`. A game update can require an app update too.

FOV above 80 and detail overlays are experimental. Detail overlays may leave weapon camos visible. Automatic recovery has automated test coverage, while map and lobby transitions still need broader in-game testing. Use game modifications where they are permitted. Anti-cheat compatibility has not been established.

## Building from source

You'll need Windows and the .NET 8 SDK.

```powershell
pwsh -File scripts/Build-Preview.ps1
```

The build is in `out/release-1.1`. This developer build needs the .NET 8 Desktop Runtime for x64.

To create the single-file downloads, run either command below.

```powershell
pwsh -File scripts/Build-Download.ps1 -Mode Standalone
pwsh -File scripts/Build-Download.ps1 -Mode Small
```

The EXEs are in `out/packaging-1.1/Standalone` and `out/packaging-1.1/Small`. Third-party notices are embedded in the app and available in [docs/licenses](docs/licenses).

See [testing](docs/FINAL-TEST-GUIDE.md), [test results](docs/TEST-REPORT.md) and [version history](CHANGELOG.md) for more detail.
