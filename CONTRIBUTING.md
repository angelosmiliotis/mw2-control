# Working on MW2 Control

Use Windows and the .NET 8 SDK. The app uses WPF and targets Windows x64.

## Build

```powershell
dotnet build src/MW2Control.App/MW2Control.App.csproj -c Release
```

Create the standalone download, including the .NET runtime:

```powershell
pwsh -File scripts/Build-Download.ps1
```

The EXE is written to `out/packaging-1.3/Standalone`. Third-party license notices are embedded in the app and stored in [docs/licenses](docs/licenses).

To replace your Desktop copy with that build:

```powershell
pwsh -File scripts/Install-Desktop.ps1
```

It checks the version and checksum, saves the previous EXE under Documents/MW2 Control Archive and installs the new EXE in Desktop/MW2 Control. Close the Desktop copy first. Saved preferences are kept separately.

## Run the checks

```powershell
dotnet run --project tests/MW2Control.Tests/MW2Control.Tests.csproj -c Release
dotnet run --project tests/MW2Control.Advanced.Tests/MW2Control.Advanced.Tests.csproj -c Release
dotnet run --project tests/MW2Control.App.Tests/MW2Control.App.Tests.csproj -c Release --no-launch-profile
dotnet run --project tests/MW2Control.Network.Tests/MW2Control.Network.Tests.csproj -c Release
```

These checks use test memory and network adapters. They cover settings validation, rollback, saved preferences, connection detection, recovery, packet parsing and firewall requests.

The [core](src/MW2Control.Core/README.md) and [network](src/MW2Control.Network/README.md) notes explain the implementation.

## UI screenshots

```powershell
dotnet run --project tools/MW2Control.Screenshots/MW2Control.Screenshots.csproj -c Release -- out/screenshots-1.3
```

This renders the actual application with an isolated, disconnected engine. It checks the control bindings, native window border and six window sizes without opening a visible window or accessing a game, saved user settings, packet capture or firewall rules.

For a retention CPU and native-read comparison, see the [benchmark tool](tools/MW2Control.Benchmarks).
