<#
.SYNOPSIS
  Builds the B2M distribution: the add-in for Revit 2024-2027, the service wheel, licences and install scripts.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build.ps1
  Produces dist\B2M-<version>\ and dist\B2M-<version>.zip
#>
param(
    [string]$Configuration = "Release",
    [string]$Out = (Join-Path $PSScriptRoot "..\dist")
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (Test-Path $dotnet) { $env:DOTNET_ROOT = Split-Path $dotnet } else { $dotnet = "dotnet" }
$python = Join-Path $root "service\.venv\Scripts\python.exe"
if (-not (Test-Path $python)) { $python = "python" }

$version = (Select-String -Path (Join-Path $root "service\pyproject.toml") -Pattern '^version = "(.+)"').Matches[0].Groups[1].Value
$package = Join-Path $Out "B2M-$version"
if (Test-Path $package) { Remove-Item -Recurse -Force $package }
New-Item -ItemType Directory -Force $package | Out-Null

$years = @{ "R24" = "2024"; "R25" = "2025"; "R26" = "2026"; "R27" = "2027" }
foreach ($code in $years.Keys | Sort-Object) {
    $year = $years[$code]
    $project = Join-Path $root "addin\B2M.Revit.$code\B2M.Revit.$code.csproj"
    Write-Host "Building add-in for Revit $year"
    & $dotnet build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Add-in build for Revit $year failed." }
    $bin = Get-ChildItem (Join-Path $root "addin\B2M.Revit.$code\bin\$Configuration") -Directory | Select-Object -First 1
    $target = Join-Path $package "addin\$year"
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($file in "B2M.Revit.dll", "Newtonsoft.Json.dll") { Copy-Item (Join-Path $bin.FullName $file) $target }
    Copy-Item (Join-Path $root "addin\B2M.Revit.addin") $target
}

Write-Host "Building the service wheel"
& $python -m pip wheel (Join-Path $root "service") --no-deps -q -w (Join-Path $package "service")
if ($LASTEXITCODE -ne 0) { throw "Service wheel build failed." }

New-Item -ItemType Directory -Force (Join-Path $package "licenses") | Out-Null
Copy-Item (Join-Path $root "NOTICE") (Join-Path $package "licenses\NOTICE")
Copy-Item (Join-Path $root "third_party\rvt-mcp\LICENSE") (Join-Path $package "licenses\rvt-mcp-LICENSE")
foreach ($file in "install.ps1", "uninstall.ps1", "standards.example.json") { Copy-Item (Join-Path $PSScriptRoot $file) $package }
Copy-Item (Join-Path $root "README.md") $package
Copy-Item (Join-Path $root "docs\quick-start.md") (Join-Path $package "QUICK-START.md")

$zip = "$package.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $package "*") -DestinationPath $zip
Write-Host "Built $package and $zip"
