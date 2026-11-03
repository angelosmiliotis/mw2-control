param(
    [ValidateSet('first-preview', 'release-candidate', 'repair-0.3', 'preview-0.4', 'preview-0.4.1', 'preview-0.4.2', 'preview-0.4.3')]
    [string]$Flavor = 'preview-0.4.3'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appProject = Join-Path $projectRoot 'src\MW2Control.App\MW2Control.App.csproj'
$outputDirectory = Join-Path (Join-Path $projectRoot 'out') $Flavor

if (-not (Test-Path -LiteralPath $appProject)) {
    throw 'The app project is not present yet. Wait for implementation to finish.'
}

& dotnet publish $appProject -c Release -r win-x64 --self-contained false -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Preview publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\START-HERE-GR.txt') -Destination (Join-Path $outputDirectory 'START-HERE-GR.txt')
Write-Output ('Preview: ' + (Join-Path $outputDirectory 'MW2Control.exe'))
