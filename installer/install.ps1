<#
.SYNOPSIS
  Installs B2M for the current user (no administrator rights): the Revit add-in for every installed Revit year
  that this package supports, and the B2M service in %LOCALAPPDATA%\B2M.
.DESCRIPTION
  Run from the unzipped package folder. Revit must be closed (it locks the add-in file).
  Needs Python 3.12 and internet access for the service's Python packages.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
#>
param(
    [string]$AddinsRoot = (Join-Path $env:APPDATA "Autodesk\Revit\Addins"),
    [string]$B2MHome = (Join-Path $env:LOCALAPPDATA "B2M"),
    [string[]]$Years,                # default: every installed Revit year this package has an add-in for
    [string]$Python = "",            # default: the Python 3.12 launcher
    [switch]$SkipService
)
$ErrorActionPreference = "Stop"
$package = $PSScriptRoot

# Revit locks the add-in it has loaded; only the real add-ins folder can be locked.
if (-not $PSBoundParameters.ContainsKey("AddinsRoot") -and (Get-Process Revit -ErrorAction SilentlyContinue)) {
    throw "Close Revit before installing: Revit locks the B2M add-in while it runs."
}

$available = Get-ChildItem (Join-Path $package "addin") -Directory | ForEach-Object Name
if (-not $Years) {
    $Years = $available | Where-Object {
        (Test-Path "HKLM:\SOFTWARE\Autodesk\Revit\$_") -or (Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $_"))
    }
}
if (-not $Years) { Write-Warning "No supported Revit year (2024-2027) found; installing the service only." }

foreach ($year in $Years) {
    if ($available -notcontains $year) { throw "This package has no add-in for Revit $year." }
    $target = Join-Path $AddinsRoot "$year\B2M"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $package "addin\$year\*.dll") $target -Force
    Copy-Item (Join-Path $package "addin\$year\B2M.Revit.addin") (Join-Path $AddinsRoot $year) -Force
    Write-Host "Installed the add-in for Revit $year"
}

New-Item -ItemType Directory -Force $B2MHome | Out-Null
Copy-Item (Join-Path $package "licenses") $B2MHome -Recurse -Force
$standards = Join-Path $B2MHome "standards.example.json"
Copy-Item (Join-Path $package "standards.example.json") $standards -Force

if (-not $SkipService) {
    if (-not $Python) {
        $launcher = Get-Command py -ErrorAction SilentlyContinue
        if (-not $launcher) { throw "Python 3.12 is needed. Install it from python.org, then run this again." }
        $Python = (& py -3.12 -c "import sys; print(sys.executable)").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Python 3.12 is needed. Install it from python.org, then run this again." }
    }
    $venv = Join-Path $B2MHome "service"
    if (Test-Path $venv) { Remove-Item -Recurse -Force $venv }
    & $Python -m venv $venv
    $wheel = Get-ChildItem (Join-Path $package "service") -Filter "b2m-*.whl" | Select-Object -First 1
    & (Join-Path $venv "Scripts\python.exe") -m pip install -q $wheel.FullName
    if ($LASTEXITCODE -ne 0) { throw "Installing the B2M service failed." }
    $start = Join-Path $B2MHome "start-b2m.cmd"
    Set-Content -Path $start -Encoding ASCII -Value "@echo off`r`n`"$venv\Scripts\python.exe`" -m b2m.api"
    Write-Host "Installed the service. Start it with $start, then open http://127.0.0.1:8765"
}
Write-Host "Done. Set ANTHROPIC_API_KEY for brief reading; copy $standards to standards.json to map project types."
