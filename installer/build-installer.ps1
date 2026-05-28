Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot "src"
$appProjectPath = Join-Path $srcRoot "OptiSYS.App\OptiSYS.App.csproj"
$installerProjectPath = Join-Path $srcRoot "OptiSYS.Installer\OptiSYS.Installer.csproj"
$publishDir = Join-Path $PSScriptRoot "publish\release-win-x64"
$distDir = Join-Path $PSScriptRoot "dist"
$zipPath = Join-Path $srcRoot "OptiSYS.Installer\Resources\app.zip"
$extractPriPayloadScript = Join-Path $srcRoot "Extract-WinUiPriPayload.ps1"
$brandAssetScript = Join-Path $PSScriptRoot "assets\generate_brand_assets.py"

Write-Host "--- Generating Brand Assets ---" -ForegroundColor Cyan
python $brandAssetScript
if ($LASTEXITCODE -ne 0) {
    throw "Brand asset generation failed."
}

# Clean previous build artifacts
Write-Host "--- Cleaning Up Old Builds ---" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Push-Location $srcRoot
try {
    # Build solution
    Write-Host "--- Building Solution ---" -ForegroundColor Cyan
    dotnet build OptiSYS.sln
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }

    # Run automated tests
    Write-Host "--- Running Tests ---" -ForegroundColor Cyan
    dotnet test OptiSYS.Tests\OptiSYS.Tests.csproj
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed."
    }

    # Publish OptiSYS.App
    Write-Host "--- Publishing OptiSYS.App ---" -ForegroundColor Cyan
    dotnet publish $appProjectPath -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }
}
finally {
    Pop-Location
}

# Run WinUI PRI Extraction script
Write-Host "--- Extracting WinUI Resources ---" -ForegroundColor Cyan
$exePath = Join-Path $publishDir "OptiSYS.exe"
if (-not (Test-Path $exePath)) {
    throw "Publish output is missing '$exePath'."
}

$priPath = Join-Path $publishDir "Microsoft.UI.Xaml.Controls.pri"
if (-not (Test-Path $priPath)) {
    throw "Publish output is missing '$priPath'."
}

if (-not (Test-Path $extractPriPayloadScript)) {
    throw "WinUI PRI extraction script was not found at '$extractPriPayloadScript'."
}

& $extractPriPayloadScript `
    -PriPath $priPath `
    -OutputRoot $publishDir `
    -WindowsSdkDir $env:WindowsSdkDir `
    -NuGetPackageRoot $env:NUGET_PACKAGES

# Validate all files are present in publish dir before zipping
$requiredWinUiPayload = @(
    "Microsoft.UI.Xaml\Themes\generic.xbf",
    "Microsoft.UI.Xaml\Themes\themeresources.xbf",
    "Microsoft.UI.Xaml\DensityStyles\Compact.xbf",
    "Microsoft.UI.Xaml\DensityStyles\CompactDatePickerTimePickerFlyout.xbf"
)

$requiredCompiledXaml = @(
    "App.xaml",
    "App.xbf",
    "MainWindow.xaml",
    "MainWindow.xbf"
)

$missingPayload = @(
    $requiredWinUiPayload |
        Where-Object { -not (Test-Path (Join-Path $publishDir $_)) }
)
if ($missingPayload.Count -gt 0) {
    throw "Publish output is missing required WinUI payload files: $($missingPayload -join ', ')"
}

$missingCompiledXaml = @(
    $requiredCompiledXaml |
        Where-Object { -not (Test-Path (Join-Path $publishDir $_)) }
)
if ($missingCompiledXaml.Count -gt 0) {
    throw "Publish output is missing compiled XAML artifacts: $($missingCompiledXaml -join ', ')"
}

# Zip the published application folder
Write-Host "--- Archiving Application Payload ---" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
# Using System.IO.Compression.ZipFile to create the archive cleanly without full path issues
[System.Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

if (-not (Test-Path $zipPath)) {
    throw "Failed to create ZIP package at '$zipPath'."
}
Write-Host "Successfully packaged app.zip: $((Get-Item $zipPath).Length / 1MB -as [int]) MB" -ForegroundColor Green

# Publish the Custom WPF Installer Project as a Single File EXE
Write-Host "--- Publishing Custom Fluent Installer ---" -ForegroundColor Cyan
Push-Location $srcRoot
try {
    $outExe = Join-Path $distDir "optiSYS-setup.exe"
    if (Test-Path $outExe) { Remove-Item $outExe -Force }

    dotnet publish $installerProjectPath -c Release -r win-x64 --self-contained true -o $distDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish for installer failed."
    }

    # Rename the output executable to optiSYS-setup.exe
    $compiledInstallerExe = Join-Path $distDir "OptiSYS.Installer.exe"
    if (Test-Path $compiledInstallerExe) {
        Move-Item $compiledInstallerExe $outExe -Force
    }
    
    # Remove extra files in dist (pdb, json configs, etc.) to keep a clean installer output directory
    Get-ChildItem $distDir | Where-Object { $_.Name -ne "optiSYS-setup.exe" } | Remove-Item -Recurse -Force
}
finally {
    Pop-Location
}

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host " SUCCESS: Animated Fluent Installer Built Successfully!" -ForegroundColor Green
Write-Host " Installer Output: $(Join-Path $distDir 'optiSYS-setup.exe')" -ForegroundColor Green
Write-Host " Size: $(([System.IO.FileInfo](Join-Path $distDir 'optiSYS-setup.exe')).Length / 1MB -as [int]) MB" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green

Get-ChildItem $distDir | Select-Object Name, Length, LastWriteTime
