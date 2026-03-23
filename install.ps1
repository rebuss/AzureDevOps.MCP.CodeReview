<#
.SYNOPSIS
    Installs REBUSS.Pure as a global .NET tool from the latest GitHub Release.

.EXAMPLE
    irm https://raw.githubusercontent.com/rebuss/CodeReview.MCP/master/install.ps1 | iex
#>

$ErrorActionPreference = "Stop"

$repo    = "rebuss/CodeReview.MCP"
$toolId  = "CodeReview.MCP"
$cmd     = "rebuss-pure"

Write-Host "Fetching latest release from GitHub..." -ForegroundColor Cyan

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$version = $release.tag_name -replace '^v', ''
$asset   = $release.assets | Where-Object { $_.name -like "*.nupkg" } | Select-Object -First 1

if (-not $asset) {
    Write-Error "No .nupkg found in release $($release.tag_name). Aborting."
    exit 1
}

$tmpDir  = Join-Path $env:TEMP "rebuss-pure-install"
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$nupkg   = Join-Path $tmpDir $asset.name

Write-Host "Downloading $($asset.name)..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $nupkg

# Uninstall previous version if present
$existing = dotnet tool list -g | Select-String $toolId
if ($existing) {
    Write-Host "Uninstalling previous version..." -ForegroundColor Yellow
    dotnet tool uninstall -g $toolId
}

Write-Host "Installing $toolId $version..." -ForegroundColor Cyan
dotnet tool install -g $toolId --add-source $tmpDir --version $version

Remove-Item -Recurse -Force $tmpDir

Write-Host ""
Write-Host "Installation complete. Run: $cmd --help" -ForegroundColor Green
