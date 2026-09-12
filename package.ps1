param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root "BestAutoSort.csproj"

# Версия из csproj
[xml]$xml = Get-Content $csproj
$version = $xml.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { $version = "0.1.0" }

Write-Host "Building BestAutoSort $version ($Configuration)..." -ForegroundColor Cyan
dotnet build $csproj -c $Configuration -p:Deploy=false -p:Package=true

$pkgDir = Join-Path $root "bin/$Configuration/net472/package"
$zipPath = Join-Path $root "BestAutoSort-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path "$pkgDir/*" -DestinationPath $zipPath
Write-Host "Packed: $zipPath" -ForegroundColor Green
Write-Host "Contents:" -ForegroundColor Gray
Get-ChildItem $pkgDir -Recurse | ForEach-Object { Write-Host "  $($_.FullName.Replace($pkgDir,''))" }
