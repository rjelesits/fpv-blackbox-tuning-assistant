$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'FPVBlackboxTuningAssistant.csproj'
$output = Join-Path $PSScriptRoot 'beta-win-x64-0.2.0-beta.18'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw 'Das .NET 10 SDK wurde nicht gefunden. Installiere es oder ergänze dotnet.exe im PATH.'
}
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME 'packages'
$env:APPDATA = Join-Path $PSScriptRoot '.appdata'
$nugetConfig = Join-Path $env:APPDATA 'NuGet\NuGet.Config'

New-Item -ItemType Directory -Force -Path (Split-Path $nugetConfig), $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null
Set-Content -LiteralPath $nugetConfig -Value '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources></configuration>' -Encoding utf8

& $dotnetExe publish $project -c Release --self-contained false -p:RestoreConfigFile=$nugetConfig -p:PublishReadyToRun=false -o $output
if ($LASTEXITCODE -ne 0) {
    throw "Beta-Build fehlgeschlagen (dotnet exit code $LASTEXITCODE)."
}

Copy-Item (Join-Path $PSScriptRoot 'README.md') $output -Force
Copy-Item (Join-Path $PSScriptRoot 'VORAUSSETZUNGEN.txt') $output -Force
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') $output -Force
Copy-Item (Join-Path $PSScriptRoot 'TUNING-WORKFLOW.md') $output -Force
$package = Join-Path $PSScriptRoot 'FPV-Blackbox-Tuning-Assistant-Beta-0.2.0-beta.18-win-x64.zip'
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $package -CompressionLevel Optimal -Force
Write-Host "Beta-Paket erstellt: $package"
