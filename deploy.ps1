#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'deploy'),
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'Huginn\Huginn.csproj'
$framework = 'net10.0-windows10.0.17763.0'

Write-Host "Publishing $Runtime to $OutputPath..." -ForegroundColor Cyan

dotnet publish $projectPath `
    -c Release `
    -f $framework `
    -r $Runtime `
    --self-contained `
    -p:PublishSingleFile=true `
    -o $OutputPath `
    -nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $OutputPath 'Huginn.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Done. $exe ($size MB)" -ForegroundColor Green
