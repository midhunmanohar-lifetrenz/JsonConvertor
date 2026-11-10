<#
.SYNOPSIS
  Builds the self-contained, single-file Windows x64 EXE (no .NET SDK, Visual Studio or Excel needed to run it).
.EXAMPLE
  ./publish.ps1                # Windows x64 EXE -> dist\win-x64\JSONConverterPro.exe
  ./publish.ps1 -MacArm64      # also build the macOS Apple Silicon binary (use publish.sh on a Mac to get the .app/.dmg)
#>
param([switch]$MacArm64)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$project = 'JSONConverterPro.App/JSONConverterPro.App.csproj'
$common = @('-c','Release','--self-contained','true','-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true',
            '-p:DebugType=none','-p:DebugSymbols=false')

if (Test-Path dist/win-x64) { Remove-Item dist/win-x64 -Recurse -Force }
dotnet publish $project @common -r win-x64 -o dist/win-x64
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
Write-Host "Built dist\win-x64\JSONConverterPro.exe" -ForegroundColor Green

if ($MacArm64) {
  dotnet publish $project @common -r osx-arm64 -o dist/osx-arm64
  if ($LASTEXITCODE -ne 0) { throw 'macOS publish failed' }
  Write-Host "Built dist\osx-arm64\JSONConverterPro (wrap into a .app with publish.sh on a Mac)" -ForegroundColor Green
}
