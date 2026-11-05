# Version history

## 1.3 - November 5, 2026

- Extends the FOV slider to 130, with 110 and 120 presets.
- Adds lightweight checks between the full session checks, so recognized game resets are caught sooner.
- Keeps full validation before every recovery write and avoids repeated writes when values already match.
- Notifies the renderer when Fullbright changes, including when restoring the original lighting.
- Adds a native-process retention benchmark and a script for replacing the Desktop copy with a verified build.

## 1.2 - November 5, 2026

- A new olive-green interface with military artwork and clearer controls.
- Separate tiles for each visual setting, with icons and switches.
- Keeps the specular selector together with its label.
- Scales the interface to fit different window sizes and aspect ratios.
- Removes the white strip above the custom title bar.
- Adds FOV and FPS presets and refreshes Diagnostics and Lobby Network.
- Includes new high-resolution screenshots of all three pages.

## 1.1 - November 4, 2026

- Downloads as a single EXE with the .NET runtime included.
- Checks every requested setting before Apply starts writing.
- Restores the previous settings after an interrupted Apply when the values can still be verified.
- Keeps the previous successful settings active when a new request fails preflight.
- Lets you prepare settings before opening the game.
- Opens the existing app window when launched a second time.
- Requests administrator access for Block or Unblock when needed.
- Remembers the network adapter used for a successful scan.
- Gives clearer explanations for missing Npcap and lobby connections.

## 1.0 - November 4, 2026

- Remembers your settings between launches.
- Detects the running game automatically.
- Recovers recognized settings resets in the same game session.
- Simplifies Lobby Network and the blocked-address list.
- Keeps Settings on one page, with details in Diagnostics.
- Updates the download guide and screenshots.

## 0.4.3 - November 3, 2026

The earlier public build, with FOV and FPS controls, stream mute, visual settings, numeric ping and lobby scanning.
