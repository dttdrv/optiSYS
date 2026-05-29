Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Builds the shippable optiSYS installer end-to-end: refresh brand assets -> build + test ->
# publish the app (Release, self-contained) -> compile the Inno Setup installer.
#
# (The old WPF "Animated Fluent" installer + its app.zip pipeline were retired in favor of the
# small, native, robust Inno installer. The OptiSYS.Installer project is no longer built here.)

$repoRoot   = Split-Path -Parent $PSScriptRoot
$srcRoot    = Join-Path $repoRoot "src"
$appProject = Join-Path $srcRoot "OptiSYS.App\OptiSYS.App.csproj"
$publishDir = Join-Path $PSScriptRoot "publish\release-win-x64"
$distDir    = Join-Path $PSScriptRoot "dist"
$issPath    = Join-Path $PSScriptRoot "OptiSYS.iss"
$brandScript = Join-Path $PSScriptRoot "assets\generate-brand-assets.ps1"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found — install from https://jrsoftware.org/isdl.php" }

Write-Host "--- Brand assets (matches the current app icon) ---" -ForegroundColor Cyan
powershell -NoProfile -ExecutionPolicy Bypass -File $brandScript
if ($LASTEXITCODE -ne 0) { throw "brand asset generation failed" }

Push-Location $srcRoot
try {
    Write-Host "--- Build + Test ---" -ForegroundColor Cyan
    dotnet build OptiSYS.sln -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    dotnet test OptiSYS.Tests\OptiSYS.Tests.csproj -c Debug --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }
}
finally { Pop-Location }

Write-Host "--- Publish app (Release, self-contained win-x64) ---" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $publishDir --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Guard the headless-PRI XAML-copy regression: the published app crashes without this.
$controlXbf = Join-Path $publishDir "Controls\HistoryChartControl.xbf"
if (-not (Test-Path $controlXbf)) {
    throw "Publish is missing '$controlXbf' — the compiled-XAML copy target regressed (Directory.Build.targets)."
}

Write-Host "--- Compile installer (Inno Setup) ---" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
& $iscc $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed" }

$setup = Get-ChildItem $distDir -Filter "optiSYS-*-setup.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`n==================================================" -ForegroundColor Green
Write-Host " SUCCESS: $($setup.Name) ($([int]($setup.Length / 1MB)) MB)" -ForegroundColor Green
Write-Host " $($setup.FullName)" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
