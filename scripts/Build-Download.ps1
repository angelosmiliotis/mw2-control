param(
    [ValidateSet('Standalone', 'Small')]
    [string]$Mode = 'Standalone'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appProject = Join-Path $projectRoot 'src\MW2Control.App\MW2Control.App.csproj'
$project = [xml](Get-Content -LiteralPath $appProject -Raw)
$version = ([version]$project.Project.PropertyGroup.Version).ToString(2)
$outputDirectory = Join-Path $projectRoot "out\packaging-$version\$Mode"
$standalone = $Mode -eq 'Standalone'
$selfContained = $standalone.ToString().ToLowerInvariant()

& dotnet publish $appProject -c Release -r win-x64 --self-contained $selfContained -m:1 `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -p:SatelliteResourceLanguages=en "-p:IncludeNativeLibrariesForSelfExtract=$selfContained" `
    "-p:EnableCompressionInSingleFile=$selfContained" -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Download build failed.' }

$executable = Join-Path $outputDirectory 'MW2Control.exe'
$extraFiles = Get-ChildItem -LiteralPath $outputDirectory -Recurse -File | Where-Object FullName -ne $executable
if ($extraFiles) { throw 'The output contains extra files. Use a fresh output directory before distributing it.' }
$size = [math]::Round((Get-Item -LiteralPath $executable).Length / 1MB, 2)
Write-Output "Built $Mode download ($size MB): $executable"
if (-not $standalone) { Write-Output 'Requires .NET 8 Desktop Runtime (x64) on the target PC.' }
