# Builds the portable release and writes latest.json next to it.
# The version comes from the app project, so the manifest can never disagree with the binary.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

[xml]$project = Get-Content 'src\SchoolPdfSorter.App\SchoolPdfSorter.App.csproj'
$version = @($project.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in SchoolPdfSorter.App.csproj.' }

if (Get-Process -Name SchoolPdfSorterApp -ErrorAction SilentlyContinue) {
    throw 'Close the running School PDF Sorter before publishing: the exe is locked.'
}

& $dotnet publish 'src\SchoolPdfSorter.App\SchoolPdfSorter.App.csproj' -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o publish

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $root 'publish\SchoolPdfSorterApp.exe'
$size = (Get-Item $exe).Length
$hash = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
$asset = "https://github.com/Tayo-Aina/SchoolPdfSorter/releases/download/v$version/SchoolPdfSorterApp.exe"

$manifest = [ordered]@{
    version = $version
    url     = $asset
    size    = $size
    sha256  = $hash
    notes   = ''
} | ConvertTo-Json

Set-Content -Path (Join-Path $root 'latest.json') -Value $manifest -Encoding utf8

Write-Host ''
Write-Host "Published $version  ($([math]::Round($size / 1MB, 1)) MB)"
Write-Host "SHA-256  $hash"
Write-Host ''
Write-Host 'Next:'
Write-Host "  1. Upload publish\SchoolPdfSorterApp.exe to GitHub release v$version"
Write-Host "  2. git add latest.json && git commit -m \"Release $version\" && git push"
Write-Host ''
