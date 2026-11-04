# Test results

Last checked on November 4, 2026, for version 1.0.

- Release build completed with no warnings or errors.
- Core checks passed 25 of 25 cases.
- Advanced settings and recovery checks passed 62 of 62 cases.
- Network checks passed 27 of 27 cases.
- The app checks passed the maintenance, saved-settings, connection-detection and recovery scenarios.

The automated checks use test adapters. They exercise missing or invalid data, process changes, short writes, rollback, recognized resets, cancellation and corrupted settings files. They do not establish in-game effects or anti-cheat compatibility.

The app was also opened on Windows and its Settings, Diagnostics and Lobby Network pages were checked. Settings fit at the minimum supported window size. Automatic detection correctly showed the game as closed.

Stream mute was reported working in multiplayer, with footsteps, gunfire, explosions and the bomb timer still audible. The latest recovery changes still need broader testing through real map and lobby transitions. Real Npcap access, relay behavior and firewall blocks also depend on the user's setup.

The commands and manual checks are in [Testing changes](FINAL-TEST-GUIDE.md).
