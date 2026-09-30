# One-command build: self-contained publish (with ReadyToRun) -> Inno Setup compile setup.exe
# Usage: powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
# NOTE: keep this file ASCII-only; Windows PowerShell reads .ps1 as ANSI and non-ASCII breaks parsing.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# 1) locate dotnet (local CLI lives under the user profile)
$dotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$publishDir = Join-Path $root 'bin\Publish\win-x64'

Write-Host '==> [1/2] Publishing self-contained (Release / win-x64 / ReadyToRun)...' -ForegroundColor Cyan
& $dotnet publish 'WinFormsApp1.csproj' -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

# 2) locate ISCC.exe (winget installs per-user under LOCALAPPDATA\Programs; also support Program Files)
Write-Host '==> [2/2] Compiling setup.exe with Inno Setup...' -ForegroundColor Cyan
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe not found. Install Inno Setup 6 first: winget install JRSoftware.InnoSetup' }

# resolve the .iss by wildcard so this script stays ASCII-only
$iss = Get-ChildItem -Path (Join-Path $root 'installer') -Filter '*.iss' | Select-Object -First 1
if (-not $iss) { throw 'No .iss found under installer\' }

& $iscc $iss.FullName
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed with exit code $LASTEXITCODE" }

$outDir = Join-Path $root 'bin\Publish\Installer'
$exe = Get-ChildItem -Path $outDir -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($exe) {
    $mb = [math]::Round($exe.Length / 1MB, 1)
    Write-Host ('==> Done: ' + $exe.FullName + '  (' + $mb + ' MB)') -ForegroundColor Green
} else {
    Write-Host '==> Compile finished but no setup.exe found; check OutputDir/OutputBaseFilename in the .iss.' -ForegroundColor Yellow
}
