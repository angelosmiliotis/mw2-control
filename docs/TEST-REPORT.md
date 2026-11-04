# Test results

Version 1.1 development build, checked on November 4, 2026.

- Release build completed with no warnings or errors.
- Core checks passed 25 of 25 cases.
- Advanced settings and recovery checks passed 67 of 67 cases.
- Network checks passed 27 of 27 cases.
- Headless app checks passed saved-settings, staging, connection detection, failed Apply, recovery, firewall action routing, bounded messages and single-instance activation.

The memory and firewall checks use test adapters. Single-instance checks use isolated local pipes and mutexes with window activation disabled. They include a malformed-message case and verify that the listener still accepts the next valid request.

The real game, live capture and real firewall were outside this test run. The Windows administrator prompt and actual window activation still need manual checking. In-game FOV effects, map and lobby transitions also need testing. Anti-cheat compatibility has not been established.

Earlier multiplayer feedback reported stream mute working while footsteps, gunfire, explosions and the bomb timer remained audible.

The commands and manual checks are in [Testing changes](FINAL-TEST-GUIDE.md).
