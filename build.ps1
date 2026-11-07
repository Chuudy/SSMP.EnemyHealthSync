# Build script for SSMP.EnemyHealthSync
# Run: .\build.ps1 [-Configuration Release]
# If SilksongPath.props exists (see SilksongPath.props.example), the DLL is also copied into
# <SilksongPluginsFolder>\SSMP.EnemyHealthSync.

param([string]$Configuration = "Debug")

$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "dotnet not found. Install the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path "SilksongPath.props")) {
    Write-Host "No SilksongPath.props: building without copying into the game." -ForegroundColor Yellow
}

& dotnet build "SSMP.EnemyHealthSync\SSMPEnemyHealthSync.csproj" --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

Write-Host "Build successful: SSMP.EnemyHealthSync\bin\$Configuration\netstandard2.1\SSMPEnemyHealthSync.dll" -ForegroundColor Green
