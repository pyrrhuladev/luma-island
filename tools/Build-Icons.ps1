param(
    [string]$InputPng,
    [string]$OutputIco,
    [string]$TrayIco,
    [string]$DocumentationPng,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $InputPng) { $InputPng = Join-Path $projectRoot 'src\Luma.Island\Assets\Luma.png' }
if (-not $OutputIco) { $OutputIco = Join-Path $projectRoot 'src\Luma.Island\Assets\Luma.ico' }
if (-not $TrayIco) { $TrayIco = Join-Path $projectRoot 'src\Luma.Island\Assets\Luma.Tray.ico' }
if (-not $DocumentationPng) { $DocumentationPng = Join-Path $projectRoot 'docs\images\logo.png' }
if (-not $ReportPath) { $ReportPath = Join-Path $projectRoot 'work\branding\icon-build.json' }
if ($env:OS -ne 'Windows_NT') { throw 'Icon conversion requires Windows.' }

# Windows PowerShell supplies the built-in WPF image encoder and an STA thread.
# Use a fresh, noninteractive process when called from PowerShell 7; no settings are changed.
if ($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    $desktopPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $desktopPowerShell -NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File $PSCommandPath `
        -InputPng $InputPng -OutputIco $OutputIco -TrayIco $TrayIco -DocumentationPng $DocumentationPng -ReportPath $ReportPath
    if ($LASTEXITCODE -ne 0) { throw "Icon conversion failed with exit code $LASTEXITCODE." }
    return
}

Add-Type -AssemblyName PresentationCore, WindowsBase
[System.Windows.Media.RenderOptions]::ProcessRenderMode = [System.Windows.Interop.RenderMode]::SoftwareOnly
$InputPng = [IO.Path]::GetFullPath($InputPng)
$OutputIco = [IO.Path]::GetFullPath($OutputIco)
$TrayIco = [IO.Path]::GetFullPath($TrayIco)
$DocumentationPng = [IO.Path]::GetFullPath($DocumentationPng)
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
if (-not (Test-Path -LiteralPath $InputPng -PathType Leaf)) { throw "Source PNG was not found: $InputPng" }
if ([IO.Path]::GetExtension($InputPng) -ne '.png' -or [IO.Path]::GetExtension($DocumentationPng) -ne '.png' `
    -or [IO.Path]::GetExtension($OutputIco) -ne '.ico' -or [IO.Path]::GetExtension($TrayIco) -ne '.ico' -or [IO.Path]::GetExtension($ReportPath) -ne '.json') {
    throw 'Use PNG source/documentation, ICO icon and JSON report filenames.'
}
if ($OutputIco -eq $TrayIco -or $ReportPath -in @($OutputIco, $TrayIco, $InputPng, $DocumentationPng) `
    -or $InputPng -in @($OutputIco, $TrayIco) -or $DocumentationPng -in @($OutputIco, $TrayIco)) {
    throw 'Source, icon, tray icon and report paths must be distinct.'
}

function Get-ByteHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Get-AlphaInfo([System.Windows.Media.Imaging.BitmapSource]$Bitmap) {
    $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($Bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $pixels = [byte[]]::new($Bitmap.PixelWidth * $Bitmap.PixelHeight * 4)
    $converted.CopyPixels($pixels, $Bitmap.PixelWidth * 4, 0)
    $transparent = 0; $partial = 0; $opaque = 0
    for ($index = 3; $index -lt $pixels.Length; $index += 4) {
        if ($pixels[$index] -eq 0) { $transparent++ }
        elseif ($pixels[$index] -eq 255) { $opaque++ }
        else { $partial++ }
    }
    return [ordered]@{ transparentPixels = $transparent; partialAlphaPixels = $partial; opaquePixels = $opaque }
}
$inputBytes = [IO.File]::ReadAllBytes($InputPng)
$sourceHash = Get-ByteHash $inputBytes
$inputStream = [IO.MemoryStream]::new($inputBytes, $false)
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create($inputStream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    if ($decoder -isnot [System.Windows.Media.Imaging.PngBitmapDecoder] -or $decoder.Frames.Count -ne 1) { throw 'Use a single-frame PNG.' }
    $source = $decoder.Frames[0]
    $source.Freeze()
} finally { $inputStream.Dispose() }
if ($source.PixelWidth -ne $source.PixelHeight -or $source.PixelWidth -lt 256 -or $source.PixelWidth -gt 8192) {
    throw 'The source must be square and between 256 and 8192 pixels. No cropping is performed.'
}
$sourceAlpha = Get-AlphaInfo $source
if ($sourceAlpha.opaquePixels -eq 0 -and $sourceAlpha.partialAlphaPixels -eq 0) { throw 'The source image is fully transparent.' }

function New-IconFrames([int[]]$Sizes) {
    foreach ($size in $Sizes) {
        $visual = [System.Windows.Media.DrawingVisual]::new()
        [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
        $drawing = $visual.RenderOpen()
        try { $drawing.DrawImage($source, [System.Windows.Rect]::new(0, 0, $size, $size)) }
        finally { $drawing.Close() }
        $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $bitmap.Freeze()
        $alpha = Get-AlphaInfo $bitmap
        if ($sourceAlpha.transparentPixels -gt 0 -and $alpha.transparentPixels -eq 0) { throw "Transparency was lost at $size px." }
        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $buffer = [IO.MemoryStream]::new()
        try {
            $encoder.Save($buffer)
            [pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray(); Alpha = $alpha }
        } finally { $buffer.Dispose() }
    }
}
function New-IcoBytes([object[]]$Frames) {
    $buffer = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($buffer)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$Frames.Count)
        $offset = 6 + 16 * $Frames.Count
        foreach ($frame in $Frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $Frames) { $writer.Write([byte[]]$frame.Bytes) }
        $writer.Flush()
        return ,$buffer.ToArray()
    } finally { $writer.Dispose(); $buffer.Dispose() }
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$traySizes = @(16, 20, 24, 32, 40, 48, 64)
$frames = @(New-IconFrames $sizes)
$iconBytes = New-IcoBytes $frames
$trayBytes = New-IcoBytes @($frames | Where-Object { $_.Size -in $traySizes })
# Independently render and encode again to catch accidental metadata or encoding nondeterminism.
$repeatFrames = @(New-IconFrames $sizes)
if ((Get-ByteHash $iconBytes) -ne (Get-ByteHash (New-IcoBytes $repeatFrames))) { throw 'Repeated icon conversion produced different bytes.' }
if ((Get-ByteHash $trayBytes) -ne (Get-ByteHash (New-IcoBytes @($repeatFrames | Where-Object { $_.Size -in $traySizes })))) {
    throw 'Repeated tray conversion produced different bytes.'
}
foreach ($path in @($OutputIco, $TrayIco, $DocumentationPng, $ReportPath)) { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null }
[IO.File]::WriteAllBytes($OutputIco, $iconBytes)
[IO.File]::WriteAllBytes($TrayIco, $trayBytes)
if ($InputPng -ne $DocumentationPng) { [IO.File]::WriteAllBytes($DocumentationPng, $inputBytes) }
if ((Get-ByteHash ([IO.File]::ReadAllBytes($DocumentationPng))) -ne $sourceHash -or (Get-ByteHash ([IO.File]::ReadAllBytes($InputPng))) -ne $sourceHash) {
    throw 'The documentation copy or the source PNG changed.'
}
$report = [ordered]@{
    source = [ordered]@{ width = $source.PixelWidth; height = $source.PixelHeight; bytes = $inputBytes.Length; sha256 = $sourceHash; alpha = $sourceAlpha }
    applicationIcon = [ordered]@{ sizes = $sizes; bytes = $iconBytes.Length; sha256 = (Get-ByteHash $iconBytes) }
    trayIcon = [ordered]@{ sizes = $traySizes; bytes = $trayBytes.Length; sha256 = (Get-ByteHash $trayBytes) }
    frames = @($frames | ForEach-Object { [ordered]@{ size = $_.Size; pngBytes = $_.Bytes.Length; alpha = $_.Alpha } })
    verification = [ordered]@{ repeatedConversionMatches = $true; sourceUnchanged = $true; documentationCopyMatchesSource = $true }
    conversion = 'Whole-image WPF HighQuality resampling with software rendering; PNG payloads in a multi-image ICO. No cropping, background removal, recoloring or retouching.'
}
$json = $report | ConvertTo-Json -Depth 7
[IO.File]::WriteAllText($ReportPath, $json, [Text.UTF8Encoding]::new($false))
$json
