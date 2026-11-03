param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$projectFile = Join-Path $projectRoot 'src\Luma.Island\Luma.Island.csproj'
if ($Publish) {
    [xml]$projectMetadata = Get-Content -LiteralPath $projectFile -Raw
    $releaseVersion = [string]$projectMetadata.Project.PropertyGroup.Version
    & $dotnetCommand publish $projectFile -c Release -o (Join-Path $projectRoot ('artifacts\Luma-Island-'+$releaseVersion))
} else {
    & $dotnetCommand build $projectFile -c Release
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
