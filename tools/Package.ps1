param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipPublish) {
    & (Join-Path $projectRoot 'build.ps1') -Publish
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
[xml]$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'src\Luma.Island\Luma.Island.csproj') -Raw
$version = [string]$metadata.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$publishedDirectory = Join-Path $artifactRoot ('Luma-Island-' + $version)
if (-not (Test-Path -LiteralPath (Join-Path $publishedDirectory 'Luma.Island.exe'))) { throw 'Publish the application first.' }
$dependencies = Get-Content -LiteralPath (Join-Path $publishedDirectory 'Luma.Island.deps.json') -Raw | ConvertFrom-Json
$licensedDependencies = Get-Content -LiteralPath (Join-Path $projectRoot 'docs\licenses\dependencies.json') -Raw | ConvertFrom-Json
$actualPacks = @($dependencies.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith('runtimepack.') })
$expectedPacks = @($licensedDependencies.PSObject.Properties | ForEach-Object { $_.Name + '/' + [string]$_.Value })
if (@(Compare-Object -ReferenceObject $expectedPacks -DifferenceObject $actualPacks).Count -gt 0) {
    throw 'Runtime dependencies changed. Review third-party licenses and update docs/licenses/dependencies.json before packaging.'
}
$sensorDirectory = Join-Path $publishedDirectory 'sensors'
$sensorDependencyFile = Join-Path $sensorDirectory 'Luma.SensorHost.deps.json'
if (-not (Test-Path -LiteralPath $sensorDependencyFile) -or -not (Test-Path -LiteralPath (Join-Path $sensorDirectory 'Luma.SensorHost.exe'))) {
    throw 'The optional sensor module was not published. Run build.ps1 -Publish.'
}
$sensorDependencies = Get-Content -LiteralPath $sensorDependencyFile -Raw | ConvertFrom-Json
$sensorExpected = Get-Content -LiteralPath (Join-Path $projectRoot 'docs\licenses\sensors-dependencies.json') -Raw | ConvertFrom-Json
$sensorActualKeys = @($sensorDependencies.libraries.PSObject.Properties.Name | Where-Object { -not $_.StartsWith('Luma.SensorHost/') })
$sensorExpectedKeys = @($sensorExpected.PSObject.Properties | ForEach-Object { $_.Name + '/' + [string]$_.Value })
if (@(Compare-Object -ReferenceObject $sensorExpectedKeys -DifferenceObject $sensorActualKeys).Count -gt 0) {
    throw 'Sensor dependencies changed. Review sensor licenses and update their dependency manifest before packaging.'
}
$sensorVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $sensorDirectory 'Luma.SensorHost.dll')).Version
$appVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publishedDirectory 'Luma.Island.dll')).Version
if ($sensorVersion -ne $appVersion) { throw 'The sensor module and application versions differ.' }
if (-not (Test-Path -LiteralPath (Join-Path $sensorDirectory 'docs\licenses\sensors\manifest.json'))) {
    throw 'Sensor license notices were not staged. Run build.ps1 -Publish after reviewing dependencies.'
}
$sensorLicenseRoot = Join-Path $projectRoot 'docs\licenses\sensors'
$sensorLicenseManifest = Get-Content -LiteralPath (Join-Path $sensorLicenseRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($sensorLicenseManifest.applicationVersion -ne $version) { throw 'Sensor license manifest targets another application version.' }
function Assert-LicensedFile($directory, $entry) {
    $directoryPath = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
    $filePath = [IO.Path]::GetFullPath((Join-Path $directoryPath $entry.file))
    if (-not $filePath.StartsWith($directoryPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid path in sensor license manifest.' }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or (Get-Item -LiteralPath $filePath).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw ('Licensed sensor file changed: ' + $entry.file)
    }
}
foreach ($entry in $sensorLicenseManifest.files) {
    Assert-LicensedFile $sensorLicenseRoot $entry
    Assert-LicensedFile (Join-Path $sensorDirectory 'docs\licenses\sensors') $entry
}
foreach ($package in $sensorLicenseManifest.packages) {
    foreach ($entry in $package.assets) { Assert-LicensedFile $sensorDirectory $entry }
}
$stageDirectory = Join-Path $artifactRoot ('.package-' + [Guid]::NewGuid().ToString('N'))
$releaseDirectory = Join-Path $artifactRoot 'release'
New-Item -ItemType Directory -Path $stageDirectory, $releaseDirectory -Force | Out-Null
$archiveName = 'Luma-Island-' + $version + '-win-x64.zip'
$temporaryArchive = Join-Path $releaseDirectory ([Guid]::NewGuid().ToString('N') + '.zip')
$archivePath = Join-Path $releaseDirectory $archiveName
try {
    $oldDocuments = @('README.txt', 'README.md', 'CHANGELOG.md', 'LICENSE', 'resource-comparison.txt', 'resource-comparison.md', 'docs')
    foreach ($item in Get-ChildItem -LiteralPath $publishedDirectory -Force) {
        if ($item.Name -notin $oldDocuments) { Copy-Item -LiteralPath $item.FullName -Destination $stageDirectory -Recurse }
    }
    foreach ($document in @('LICENSE', 'README.txt', 'README.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $stageDirectory
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $stageDirectory -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\performance.md') -Destination (Join-Path $stageDirectory 'resource-comparison.md')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stageDirectory, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        $files = @(Get-ChildItem -LiteralPath $stageDirectory -File -Recurse)
        if ($archive.Entries.Count -ne $files.Count) { throw 'Archive file count mismatch.' }
        foreach ($file in $files) {
            $relativeName = $file.FullName.Substring($stageDirectory.Length + 1).Replace('\', '/')
            $entry = $archive.GetEntry($relativeName)
            if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw "Archive mismatch: $relativeName" }
            $stream = $entry.Open()
            try { $actualHash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
            finally { $stream.Dispose() }
            if ($actualHash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "Archive content mismatch: $relativeName" }
        }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    $checksumPath = Join-Path $releaseDirectory ('Luma-Island-' + $version + '-SHA256SUMS.txt')
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksumPath, "$checksum  $archiveName`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Verified $($files.Count) files. Release: $archivePath"
    Write-Host "SHA256: $checksum"
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stageDirectory)
    if ((Split-Path -Parent $resolvedStage) -ne $artifactRoot -or (Split-Path -Leaf $resolvedStage) -notmatch '^\.package-[0-9a-f]{32}$') {
        throw 'Refusing to clean a staging directory outside artifacts.'
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
