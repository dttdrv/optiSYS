param(
    [Parameter(Mandatory = $true)]
    [string]$PriPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [string]$MakePriPath,
    [string]$WindowsSdkDir,
    [string]$NuGetPackageRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-MakePri {
    param(
        [string]$ExplicitPath,
        [string]$SdkRoot,
        [string]$PackagesRoot
    )

    if ($ExplicitPath -and (Test-Path -LiteralPath $ExplicitPath)) {
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $sdkRoots = @()
    if ($SdkRoot) {
        $sdkRoots += $SdkRoot
    }

    $canonicalRoot = 'C:\Program Files (x86)\Windows Kits\10\'
    if (($sdkRoots -notcontains $canonicalRoot) -and (Test-Path -LiteralPath $canonicalRoot)) {
        $sdkRoots += $canonicalRoot
    }

    foreach ($root in $sdkRoots) {
        $binRoot = Join-Path $root 'bin'
        if (-not (Test-Path -LiteralPath $binRoot)) {
            continue
        }

        $candidate = Get-ChildItem -Path $binRoot -Recurse -Filter makepri.exe -File |
            Where-Object { $_.FullName -match '\\x64\\makepri\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($candidate) {
            return $candidate.FullName
        }
    }

    $packageRoots = @()
    if ($PackagesRoot) {
        $packageRoots += $PackagesRoot
    }

    $defaultPackagesRoot = Join-Path $env:USERPROFILE '.nuget\packages'
    if (($packageRoots -notcontains $defaultPackagesRoot) -and (Test-Path -LiteralPath $defaultPackagesRoot)) {
        $packageRoots += $defaultPackagesRoot
    }

    foreach ($root in $packageRoots) {
        $buildToolsRoot = Join-Path $root 'microsoft.windows.sdk.buildtools'
        if (-not (Test-Path -LiteralPath $buildToolsRoot)) {
            continue
        }

        $candidate = Get-ChildItem -Path $buildToolsRoot -Recurse -Filter makepri.exe -File |
            Where-Object { $_.FullName -match '\\x64\\makepri\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw "Unable to locate makepri.exe. Provide -MakePriPath explicitly or install Microsoft.Windows.SDK.BuildTools."
}

function Write-EmbeddedPayload {
    param(
        [string]$PriDumpContent,
        [string]$RelativePath,
        [string]$DestinationRoot
    )

    $escapedName = [Regex]::Escape([IO.Path]::GetFileName($RelativePath))
    $pattern = '<NamedResource[^>]*name="' + $escapedName + '"[\s\S]*?<Base64Value>([\s\S]*?)</Base64Value>'
    $match = [Regex]::Match($PriDumpContent, $pattern)
    if (-not $match.Success) {
        throw "Embedded payload not found for '$RelativePath' in '$PriPath'."
    }

    $base64 = ($match.Groups[1].Value -replace '\s', '')
    $bytes = [Convert]::FromBase64String($base64)
    $destination = Join-Path $DestinationRoot $RelativePath
    $destinationDirectory = [IO.Path]::GetDirectoryName($destination)
    [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
    [IO.File]::WriteAllBytes($destination, $bytes)
}

$resolvedPriPath = (Resolve-Path -LiteralPath $PriPath).Path
$resolvedOutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$resolvedMakePriPath = Find-MakePri -ExplicitPath $MakePriPath -SdkRoot $WindowsSdkDir -PackagesRoot $NuGetPackageRoot
$priDumpPath = Join-Path $env:TEMP ("optiSYS-pri-" + [Guid]::NewGuid().ToString("N") + ".xml")

try {
    & $resolvedMakePriPath dump /if $resolvedPriPath /of $priDumpPath /dt detailed | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "makepri dump failed for '$resolvedPriPath' with exit code $LASTEXITCODE."
    }

    $priDump = Get-Content -LiteralPath $priDumpPath -Raw
    foreach ($relativePath in @(
        'Microsoft.UI.Xaml/DensityStyles/Compact.xbf',
        'Microsoft.UI.Xaml/DensityStyles/CompactDatePickerTimePickerFlyout.xbf',
        'Microsoft.UI.Xaml/Themes/generic.xbf',
        'Microsoft.UI.Xaml/Themes/themeresources.xbf'
    )) {
        Write-EmbeddedPayload -PriDumpContent $priDump -RelativePath $relativePath -DestinationRoot $resolvedOutputRoot
    }
}
finally {
    if (Test-Path -LiteralPath $priDumpPath) {
        Remove-Item -LiteralPath $priDumpPath -Force
    }
}
