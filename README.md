# MW2 Control

A small Windows app for Call of Duty: Modern Warfare 2 (2009) on Steam. Change your FOV and FPS cap, quiet the background audio, and keep your settings in one place.

**[Download for Windows](https://github.com/angelosmiliotis/mw2-control/releases/latest)** · [Report a bug](https://github.com/angelosmiliotis/mw2-control/issues/new?template=bug-report.yml) · [Suggest something](https://github.com/angelosmiliotis/mw2-control/issues/new?template=feedback.yml)

![Settings in MW2 Control](docs/screenshots/settings.png)

## Getting started

1. Download the Windows x64 ZIP from Releases and extract it.
2. Start MW2 Multiplayer through Steam, then open `MW2Control.exe`.
3. Pick your settings and click **Apply**.

Your choices are saved when you Apply or close the app. Apply also keeps the settings active and retries recognized resets while the same game process is running. If you restart the game, click Apply again. **Reset** stops this and restores the original values where they can still be verified.

The download includes the .NET 8 Desktop Runtime. Keep the extracted files together.

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

To block or unblock an address, open MW2 Control as administrator. Blocking can disconnect you and affect other lobbies sharing the same IP. You can remove a block from the list in the app.

## A few things to know

The app supports a specific x64 Steam build of `iw4mp.exe`. A game update can require an app update too.

FOV above 80 and detail overlays are experimental. Detail overlays may leave weapon camos visible. Automatic recovery has automated test coverage, while map and lobby transitions still need broader in-game testing. Use game modifications where they are permitted. Anti-cheat compatibility has not been established.

## Building from source

You'll need Windows and the .NET 8 SDK.

```powershell
pwsh -File scripts/Build-Preview.ps1
```

The build is in `out/release-1.0`. This developer build needs the .NET 8 Desktop Runtime for x64.

See [testing](docs/FINAL-TEST-GUIDE.md), [test results](docs/TEST-REPORT.md) and [version history](CHANGELOG.md) for more detail.
