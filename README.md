# MW2 Control

Your FOV, FPS, audio and visual settings for **Call of Duty: Modern Warfare 2 (2009)** on Steam, in one Windows app.

**[Download MW2 Control](https://github.com/angelosmiliotis/mw2-control/releases/latest)**

One EXE for Windows x64. The .NET runtime is included. Download the Standalone EXE from **Assets**; GitHub's source-code ZIP is for developers.

![MW2 Control settings](docs/screenshots/settings.png)

## Quick start

1. Open the downloaded EXE and start MW2 Multiplayer through Steam.
2. Wait for **Connected**, then choose your settings.
3. Click **Apply** and leave MW2 Control open while you play.

Your choices are remembered. Apply keeps them active and reapplies recognized game resets during the same game session. After restarting the game, click Apply again. **Reset** stops this and restores the original values where they can still be verified.

## Settings

| Setting | What it does |
| --- | --- |
| **Field of view** | Changes how much of the scene you see, from **65 to 130**. Starts at **90**. |
| **Frame cap** | Sets your FPS limit. Starts at **333**. Use **Uncapped FPS** to remove the cap. |
| **Selective mute** | Mutes lobby and match music, round announcements and match countdowns. Footsteps, killstreaks, explosions and the planted bomb timer stay audible. |
| **Fullbright** | Uses flat lighting to brighten the scene. |
| **No Fog** | Turns off fog. |
| **No Glow** | Turns off the glow effect. |
| **No detail overlays** | Hides detail texture overlays. Some weapon camos may remain visible. |
| **Specular mode** | Changes surface reflections: **Original**, **Black / off**, **Native** or **White / shiny**. Original restores the value captured by the app. |
| **Numeric ping** | Shows ping in milliseconds on the scoreboard instead of bars. |

## Lobby Network

Install [Npcap](https://npcap.com/#download) once to use lobby scanning. The other settings work without it.

Join a lobby and click **Scan lobby**. The app shows the likely host based on traffic. This is an estimate: Steam relays can hide the actual host, and an IP does not identify a player's Steam account.

Use **Block likely host** to block that address for MW2, or select a block and click **Unblock selected** to remove it. Windows asks for administrator access when needed. A block can disconnect you and affect other lobbies using the same IP. Blocks stay in place until you remove them.

## Need a hand?

MW2 Control checks for a newer stable release when it opens. If one is available, click **Update available** in the title bar to open its GitHub download page. If you have version 1.4 or earlier, download the current Standalone EXE once to start receiving update notices.

If something isn't working, open **Diagnostics** for the connection details and recent messages. A game update can require an app update too; MW2 Control supports the Steam x64 build of `iw4mp.exe`.

[Report a bug](https://github.com/angelosmiliotis/mw2-control/issues/new?template=bug-report.yml) · [Suggest a change](https://github.com/angelosmiliotis/mw2-control/issues/new?template=feedback.yml) · [Version history](CHANGELOG.md)

<details>
<summary>More screenshots</summary>

![Diagnostics](docs/screenshots/diagnostics.png)

![Lobby Network](docs/screenshots/lobby.png)

</details>

Building the app? See [Contributing](CONTRIBUTING.md). Third-party notices are in [docs/licenses](docs/licenses) and embedded in the EXE.
