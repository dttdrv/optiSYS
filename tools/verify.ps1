param(
    [switch] $SkipDotNet
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    if (-not $SkipDotNet) {
        dotnet build "src\OptiSYS.Tests\OptiSYS.Tests.csproj" -c Debug -p:Platform=x64 --no-restore --verbosity minimal
        dotnet test "src\OptiSYS.Tests\OptiSYS.Tests.csproj" -c Debug -p:Platform=x64 --no-restore --verbosity minimal
    }
}
finally {
    Pop-Location
}
