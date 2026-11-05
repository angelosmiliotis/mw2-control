# Core

The core handles game discovery, build checks and settings changes. The WPF app calls `ControlEngine`; tests supply an `IProcessSource` and `IProcessMemory` to exercise the same code without opening a game.

## Sessions and writes

A session includes the PID, process start time, executable path, module base and size, architecture and SHA-256. The engine keeps a read handle and a file handle for the inspected executable. Cached inspection avoids hashing the same held file on every refresh.

Before writing, the core checks the session, dvar pointer, exact name, value range and memory protection. A temporary write handle is used for each allowed write. Short writes and readback failures trigger rollback where the changed bytes can still be identified.

Restore uses the original values captured for that process. A replacement process needs a fresh Apply. Closing the engine releases its handles.

## Applying and keeping settings

`ApplySettings(TuningRequest, AdvancedSettings)` checks both groups before writing. It records the previous values and restore history, applies FOV/FPS, then audio, visuals and scoreboard settings. An interrupted operation rolls back through the same address and ownership guards. Successful rollback restores the previous recovery targets. Concurrent changes that cannot be attributed to the app prevent rollback and stop recovery.

`MaintainOwned(true)` performs one recovery check against the existing session. The app schedules checks after Apply. It can recover a captured baseline or a recognized reset value, including FOV 65 and FPS 85 on the supported build. Temporary failures preserve the last successful requests for another checked attempt. Unknown values wait for recognizable data. A process-lifetime change stops recovery.

Between full validation ticks, `OwnedValuesMatchReadOnly()` compares the applied target bytes through the existing read handle. This is a read-only hint; a mismatch or read failure always returns to full pointer, type, memory-protection and session checks before any write. The app polls this path at 50 ms, performs periodic full checks at 500 ms and refreshes Diagnostics at most once per second. Transient recovery failures retain the slower retry delay.

Already-matching settings are remembered as requests too. This allows a later game reset to be recovered even when the initial Apply needed no write. Original values remain separate from requested values.

`ResetSettings()` stops the app's recovery flow and restores values where the original and current bytes can still be verified. The lower-level `ResetSession()` forgets restore history and releases handles without restoring values.

The legacy single-group methods remain available. Their confirmation argument is required at the API boundary.

## Supported layout

The supported executable hash is

```text
926F8A47959F2B24420898D07F599883E0866050D42D5289F94EEF546A4A3492
```

The dvar name pointer is at +0x00, type at +0x0C, current value at +0x10 and domain at +0x40. Boolean writes are one byte. Float, integer and enum writes are four bytes. The sole additional renderer notification is `r_fullbright.modified` at +0x0D: write 1 after a Fullbright change, including restoration and rollback. The marker is validated as boolean and writable and is consumed by the engine. The app never restores it to 0 or continuously maintains it. Permission flags, latched values, defaults, domains and executable code are left intact.

| Setting | Cached pointer RVA | Type |
| --- | --- | --- |
| cg_fov | 0x809C78 | Float |
| com_maxfps | 0x1D260A8 | Integer |
| snd_enableStream | 0x65449E0 | Boolean |
| r_fullbright | 0x8CF78C8 | Boolean |
| r_fog | 0x8CF7C08 | Boolean |
| r_glow | 0x8CF7D78 | Boolean |
| r_specularMap | 0x8CF7880 | Enum |
| r_detail | 0x8CF7DB0 | Boolean |
| r_detailMap | 0x8CF78A8 | Enum |
| cg_scoreboardPingText | 0x80E5C0 | Boolean |
| cg_scoreboardPingGraph | 0x80E5F8 | Boolean |

Specular uses Black, Unchanged, White and Gray. The app requests the first three values and can restore a captured Gray value. Its table is at RVA 0x577860, with strings at 0x3C2F14, 0x3C2F20, 0x3C2F2C and 0x3C2F34. Detail-map uses Hidden and Unchanged, with table RVA 0x5779C0 and strings at 0x3C2F3C and 0x3C2F20. Runtime checks include counts, relocated pointers, exact strings and the null terminator.

Requests accepted by the app are FOV 65-130 and FPS 30-500, plus 0 for uncapped FPS. Runtime validation checks the captured registration metadata before changing current values.

## Checking the executable on disk

`Tools/inspect-profile.py` checks the hash before reading the PE file. These commands reproduce the registration and enum-table evidence.

```powershell
python src/MW2Control.Core/Tools/inspect-profile.py '<absolute iw4mp.exe path>' 0x274840:0xc0 0x274b20:0x100 0x274e40:0x2a0 0x577860:0x30 0x5779c0:0x20 0x3c2f10:0x40
python src/MW2Control.Core/Tools/inspect-profile.py '<absolute iw4mp.exe path>' 0xd75e0:0xb0 0x363db0:0x20 0x359c80:0x20 0x1f43b0:0x50 0x274c20:0x130 0x274d50:0xf0
```

The boolean registration wrapper is at 0x274840, enum wrapper at 0x274B20 and allocator at 0x274E40. FOV registration calls the float wrapper at 0x274C20. FPS registration calls the integer wrapper at 0x274D50.

The inspection tool reads the executable on disk. External process writes have a race between the final check and the native write.

See [Contributing](../../CONTRIBUTING.md) for build and check commands.
