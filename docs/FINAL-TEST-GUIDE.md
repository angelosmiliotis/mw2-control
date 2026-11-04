# Testing changes

Run the automated checks from the repository root on Windows with the .NET 8 SDK.

```powershell
dotnet build src/MW2Control.App/MW2Control.App.csproj -c Release
dotnet run --project tests/MW2Control.Tests/MW2Control.Tests.csproj -c Release
dotnet run --project tests/MW2Control.Advanced.Tests/MW2Control.Advanced.Tests.csproj -c Release
dotnet run --project tests/MW2Control.App.Tests/MW2Control.App.Tests.csproj -c Release --no-launch-profile
dotnet run --project tests/MW2Control.Network.Tests/MW2Control.Network.Tests.csproj -c Release
```

These checks use test memory and network adapters. They cover validation, partial writes, rollback, settings persistence, connection detection, automatic recovery, packet parsing and firewall-rule handling.

## Checking the app

- Open it with the game closed and check the connection indicator.
- Open the supported game build and check that the indicator changes.
- Change settings, close the app, and reopen it. Your choices should be remembered.
- Resize to the minimum window size. Settings should fit without scrolling.
- Check that Diagnostics still shows the game details and recent actions.

## Checking in-game behavior

Close other tuning tools and use a session where modifications are permitted.

1. Apply FOV, FPS and numeric ping. Check the result in the game.
2. Respawn, change maps, leave the lobby and use Alt+Tab. Check that the selected settings recover after recognized resets.
3. Change a slider without clicking Apply. The previous applied settings should remain active.
4. Click Reset and check that automatic recovery stops.
5. Restart the game. The app should detect it and wait for a fresh Apply.
6. Test stream mute with footsteps, player gunfire, explosions and the bomb timer.

Record the app version, game build, settings and relevant Diagnostics messages when reporting a problem.

## Checking Lobby Network

Npcap is required for scanning. Join a lobby and check that Scan lobby becomes Stop scan while observation is running. Check adapter selection if no traffic appears.

Block and unblock checks require administrator access and should use an address you have chosen to block. Confirm the address before adding a rule, then remove it from the app's blocked-address list. The likely host is an estimate and may be a Steam relay.
