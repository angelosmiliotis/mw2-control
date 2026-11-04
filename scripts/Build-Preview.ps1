param(
    [ValidateSet('first-preview', 'release-candidate', 'repair-0.3', 'preview-0.4', 'preview-0.4.1', 'preview-0.4.2', 'preview-0.4.3', 'preview-0.4.4', 'release-1.0', 'release-1.1', 'release-1.2')]
    [string]$Flavor = 'release-1.2'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appProject = Join-Path $projectRoot 'src\MW2Control.App\MW2Control.App.csproj'
$outputDirectory = Join-Path (Join-Path $projectRoot 'out') $Flavor

if (-not (Test-Path -LiteralPath $appProject)) {
    throw "Project file not found: $appProject"
}

& dotnet publish $appProject -c Release -r win-x64 --self-contained false -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\START-HERE.txt') -Destination (Join-Path $outputDirectory 'START-HERE.txt')
Remove-Item -LiteralPath (Join-Path $outputDirectory 'START-HERE-GR.txt') -ErrorAction SilentlyContinue
Write-Output ('Built: ' + (Join-Path $outputDirectory 'MW2Control.exe'))
