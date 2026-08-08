$ErrorActionPreference = "Stop"

$pluginName = "Tomestone.Translate"
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "Tomestone.Translate\bin\Debug"
$dest = Join-Path $env:APPDATA "XIVLauncher\devPlugins\$pluginName"

if (-not (Test-Path (Join-Path $source "$pluginName.dll"))) {
    Write-Host "Build output not found. Run 'dotnet build' first: $source" -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $source "$pluginName.dll") -Destination $dest -Force
Copy-Item (Join-Path $source "$pluginName.json") -Destination $dest -Force

Write-Host "Deployed $pluginName to:" -ForegroundColor Green
Write-Host "  $dest"
Write-Host "Restart the game (or reload Dalamud) and enable 'Tomestone Translate' in the plugin installer."