param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $SkipPublish) {
    & (Join-Path $projectRoot 'build.ps1') -Publish
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
[xml]$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'src\Luma.Island\Luma.Island.csproj') -Raw
$version = [string]$metadata.Project.PropertyGroup.Version
$publishedDirectory = Join-Path $projectRoot ('artifacts\Luma-Island-' + $version)
$resultsDirectory = Join-Path $projectRoot 'work\checks'
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$checks = @('PlacementChecks', 'AppearanceChecks', 'ComponentChecks', 'PerformanceChecks', 'CpuUsageChecks', 'HistoryChecks', 'ArtworkChecks', 'MediaArtworkChecks', 'AutostartChecks', 'AutostartUiChecks', 'GpuTemperatureChecks', 'GpuTemperatureUiChecks', 'CompactUiChecks', 'ScaleUiChecks', 'CollapsedMediaUiChecks', 'VisibilityUiChecks', 'ClickThroughUiChecks', 'CpuMemoryTemperatureUiChecks', 'SensorHostChecks', 'AppIconChecks', 'GameProfileChecks', 'GameProfileRoutingChecks', 'GameProfilesUiChecks', 'FrameTimeChecks', 'FrameViewChecks', 'FrameTimingServiceChecks', 'FrameMetricsUiChecks')
foreach ($check in $checks) {
    $project = Join-Path $projectRoot ('tests\' + $check)
    $arguments = @('run', '--project', $project, '-c', 'Release')
    if ($check -like '*UiChecks' -or $check -eq 'AppIconChecks') { $arguments += ('-p:PublishedAppDirectory=' + $publishedDirectory) }
    $arguments += @('--', (Join-Path $resultsDirectory $check))
    Write-Host "Checking $check..."
    & $dotnetCommand @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host 'All checks passed.'
