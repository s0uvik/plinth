<#
.SYNOPSIS
  Removes the B2M add-in from every Revit year and the B2M service. Runs, briefs and logs stay unless -Purge.
#>
param(
    [string]$AddinsRoot = (Join-Path $env:APPDATA "Autodesk\Revit\Addins"),
    [string]$B2MHome = (Join-Path $env:LOCALAPPDATA "B2M"),
    [switch]$Purge
)
$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("AddinsRoot") -and (Get-Process Revit -ErrorAction SilentlyContinue)) {
    throw "Close Revit first: it locks the B2M add-in."
}

foreach ($dir in Get-ChildItem $AddinsRoot -Directory -ErrorAction SilentlyContinue) {
    $manifest = Join-Path $dir.FullName "B2M.Revit.addin"
    if (Test-Path $manifest) { Remove-Item $manifest -Force }
    $folder = Join-Path $dir.FullName "B2M"
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force; Write-Host "Removed the add-in for Revit $($dir.Name)" }
}
foreach ($item in "service", "start-b2m.cmd", "licenses", "standards.example.json") {
    $path = Join-Path $B2MHome $item
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}
if ($Purge -and (Test-Path $B2MHome)) { Remove-Item $B2MHome -Recurse -Force; Write-Host "Removed $B2MHome" }
Write-Host "B2M removed."
