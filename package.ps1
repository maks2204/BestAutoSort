param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root "BestAutoSort.csproj"

# Version from csproj (first PropertyGroup that defines it).
[xml]$xml = Get-Content $csproj
$version = $xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -ExpandProperty Version -First 1
if (-not $version) { $version = "0.1.0" }

# Manifest version must match the zip name or Thunderstore rejects the package.
$manifestVersion = (Get-Content (Join-Path $root "manifest.json") -Raw | ConvertFrom-Json).version_number
if ($manifestVersion -ne $version) {
    throw "Version mismatch: csproj=$version, manifest.json=$manifestVersion. Align them first."
}

# Icon sanity check (Thunderstore expects 256x256 PNG).
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $root "icon.png"
try {
    $icon = [System.Drawing.Image]::FromFile($iconPath)
    try {
        if ($icon.Width -ne 256 -or $icon.Height -ne 256) {
            Write-Host "WARNING: icon.png is $($icon.Width)x$($icon.Height), expected 256x256." -ForegroundColor Yellow
        }
    }
    finally {
        $icon.Dispose()
    }
}
catch {
    Write-Host "WARNING: could not read icon.png: $_" -ForegroundColor Yellow
}

$pkgDir = Join-Path $root "bin/$Configuration/net472/package"
if (Test-Path $pkgDir) {
    # Drop stale files (e.g. renamed DLLs) so they never leak into the zip.
    Remove-Item $pkgDir -Recurse -Force
}

Write-Host "Building BestAutoSort $version ($Configuration)..." -ForegroundColor Cyan
dotnet build $csproj -c $Configuration -p:Deploy=false -p:Package=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE, package aborted."
}

$dllPath = Join-Path $pkgDir "plugins/BestAutoSort.dll"
if (-not (Test-Path $dllPath)) {
    throw "Expected output missing: $dllPath"
}

# Packaged README points images at GitHub (repo README keeps relative paths).
$readmePath = Join-Path $pkgDir "README.md"
if (Test-Path $readmePath) {
    $base = "https://raw.githubusercontent.com/maks2204/BestAutoSort/main/docs/images/"
    $text = Get-Content $readmePath -Raw
    $text = $text.Replace('src="docs/images/', "src=`"$base")
    $iconBase = "https://raw.githubusercontent.com/maks2204/BestAutoSort/main/"
    $text = $text.Replace('](icon.png)', "]($iconBaseicon.png)")
    Set-Content $readmePath $text -NoNewline
    Write-Host "README.md image links rewritten to GitHub." -ForegroundColor Gray
}

$zipPath = Join-Path $root "BestAutoSort-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path "$pkgDir/*" -DestinationPath $zipPath
Write-Host "Packed: $zipPath" -ForegroundColor Green
Write-Host "Contents:" -ForegroundColor Gray
Get-ChildItem $pkgDir -Recurse | ForEach-Object { Write-Host "  $($_.FullName.Replace($pkgDir,''))" }
