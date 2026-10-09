# Builds Nighty in Release and publishes a self-contained Windows x64 executable.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build Nighty.csproj -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish Nighty.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nDone. Run: publish\Nighty.exe"
