param(
    [string]$Source
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Desktop installation is available on Windows.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\MW2Control.App\MW2Control.App.csproj') -Raw)
$projectVersion = [version]$project.Project.PropertyGroup.Version
$expectedVersion = [version]::new($projectVersion.Major, $projectVersion.Minor, [Math]::Max(0, $projectVersion.Build), [Math]::Max(0, $projectVersion.Revision))
if (-not $Source) { $Source = Join-Path $projectRoot "out\packaging-$($expectedVersion.ToString(2))\Standalone\MW2Control.exe" }
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$sourceInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($sourcePath)
if ([version]$sourceInfo.FileVersion -ne $expectedVersion) { throw 'Source EXE version does not match the project.' }
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$desktopDirectory = Join-Path ([Environment]::GetFolderPath('Desktop')) 'MW2 Control'
$destination = Join-Path $desktopDirectory 'MW2Control.exe'
if ($sourcePath -eq $destination) { throw 'Choose a build output as the source.' }
[IO.Directory]::CreateDirectory($desktopDirectory) | Out-Null

if (Test-Path -LiteralPath $destination) {
    $previousHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($previousHash -eq $sourceHash) {
        Write-Output "Desktop already has MW2 Control $($expectedVersion.ToString(2)) ($sourceHash)."
        return
    }
    # Refuse to replace a running/locked Desktop copy. Other build folders are unaffected.
    try { $probe = [IO.File]::Open($destination, 'Open', 'ReadWrite', 'None'); $probe.Dispose() }
    catch { throw 'Close the Desktop copy of MW2 Control before replacing it.' }
    $archiveDirectory = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ('MW2 Control Archive\' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($archiveDirectory) | Out-Null
    $backup = Join-Path $archiveDirectory 'MW2Control.exe'
    Copy-Item -LiteralPath $destination -Destination $backup
    if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $previousHash) { throw 'Backup verification failed.' }
    [ordered]@{
        previousVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion
        previousSha256 = $previousHash
        newVersion = $expectedVersion.ToString()
        newSha256 = $sourceHash
        replacedAt = (Get-Date -Format o)
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $archiveDirectory 'replacement.json')
    Write-Output "Previous Desktop EXE saved: $backup"
}

$staged = Join-Path $desktopDirectory ('MW2Control-' + [guid]::NewGuid().ToString('N') + '.new')
try {
    Copy-Item -LiteralPath $sourcePath -Destination $staged
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Staged EXE checksum mismatch.' }
    [IO.File]::Move($staged, $destination, $true)
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Desktop EXE checksum mismatch.' }
    if ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion -ne $expectedVersion) { throw 'Desktop EXE version mismatch.' }
    Write-Output "Installed MW2 Control $($expectedVersion.ToString(2)): $destination"
    Write-Output "SHA256: $sourceHash"
}
finally {
    # A failed replacement keeps its verified staged file for diagnosis/recovery.
    if (Test-Path -LiteralPath $staged) { Write-Warning "Staged EXE remains at $staged; Desktop replacement did not finish." }
}
