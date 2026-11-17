<#
.SYNOPSIS
  Removes the Plinth add-in from every Revit year and the Plinth service. Runs, briefs and logs stay unless -Purge.
#>
param(
    [string]$AddinsRoot = (Join-Path $env:APPDATA "Autodesk\Revit\Addins"),
    [string]$PlinthHome = (Join-Path $env:LOCALAPPDATA "Plinth"),
    [switch]$Purge
)
$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("AddinsRoot") -and (Get-Process Revit -ErrorAction SilentlyContinue)) {
    throw "Close Revit first: it locks the Plinth add-in."
}

foreach ($dir in Get-ChildItem $AddinsRoot -Directory -ErrorAction SilentlyContinue) {
    $manifest = Join-Path $dir.FullName "Plinth.Revit.addin"
    if (Test-Path $manifest) { Remove-Item $manifest -Force }
    $folder = Join-Path $dir.FullName "Plinth"
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force; Write-Host "Removed the add-in for Revit $($dir.Name)" }
}
foreach ($item in "service", "start-plinth.cmd", "licenses", "standards.example.json") {
    $path = Join-Path $PlinthHome $item
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}
if ($Purge -and (Test-Path $PlinthHome)) { Remove-Item $PlinthHome -Recurse -Force; Write-Host "Removed $PlinthHome" }
Write-Host "Plinth removed."
