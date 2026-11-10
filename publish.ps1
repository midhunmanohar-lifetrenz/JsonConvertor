<#
.SYNOPSIS
  Builds the Windows x64 release: self-contained single-file EXE (no .NET, Visual Studio or Excel needed),
  a portable ZIP and, with -Installer, a per-user setup EXE (Inno Setup).
.EXAMPLE
  ./publish.ps1                 # dist\win-x64\JSONConverterPro.exe + dist\release\...-portable.zip
  ./publish.ps1 -Installer      # also dist\release\JSONConverterPro-<ver>-win-x64-setup.exe
  ./publish.ps1 -MacArm64       # also the macOS Apple Silicon binary (use publish.sh on a Mac for the .app/.dmg)
#>
param([switch]$Installer, [switch]$MacArm64)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$project = 'JSONConverterPro.App/JSONConverterPro.App.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$common = @('-c','Release','--self-contained','true','-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true',
            '-p:DebugType=none','-p:DebugSymbols=false')

if (Test-Path dist/win-x64) { Remove-Item dist/win-x64 -Recurse -Force }
dotnet publish $project @common -r win-x64 -o dist/win-x64
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
Write-Host "Built dist\win-x64\JSONConverterPro.exe" -ForegroundColor Green

New-Item -ItemType Directory -Force dist/release | Out-Null
$zip = "dist/release/JSONConverterPro-$version-win-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path dist/win-x64/JSONConverterPro.exe, THIRD-PARTY-NOTICES.txt, docs/USER_GUIDE.md -DestinationPath $zip
Write-Host "Built $zip" -ForegroundColor Green

if ($Installer) {
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $iscc) {
    Write-Host 'Inno Setup not found; installing with Chocolatey...'
    choco install innosetup -y --no-progress | Out-Null
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
  }
  & $iscc "/DAppVersion=$version" installer/JSONConverterPro.iss
  if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
  Write-Host "Built dist\release\JSONConverterPro-$version-win-x64-setup.exe" -ForegroundColor Green
}

if ($MacArm64) {
  dotnet publish $project @common -r osx-arm64 -o dist/osx-arm64
  if ($LASTEXITCODE -ne 0) { throw 'macOS publish failed' }
}
