<#
.SYNOPSIS
    Builds the pyvswitch installer: the desktop app, the native CLI, an MSI and a setup EXE.

.PARAMETER NoAot
    Publish the CLI as a trimmed single-file app instead of a native AOT binary. Use this when the
    MSVC build tools ("Desktop development with C++") are not installed.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$NoAot
)

$ErrorActionPreference = "Stop"

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    [xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
    $version = $props.Project.PropertyGroup.Version
    $publishDir = Join-Path $root "artifacts\publish"
    $installerDir = Join-Path $root "artifacts\installer"
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $publishDir, $installerDir | Out-Null
    Write-Host "Building pyvswitch $version ($Runtime)" -ForegroundColor Green

    # The native AOT linker step locates MSVC through vswhere, which lives beside the VS Installer.
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }

    Invoke-Step "Restore tools" {
        dotnet tool restore
        dotnet wix extension add WixToolset.UI.wixext/5.0.2
        dotnet wix extension add WixToolset.Util.wixext/5.0.2
        dotnet wix extension add WixToolset.BootstrapperApplications.wixext/5.0.2
    }

    Invoke-Step "Test" {
        dotnet test .\tests\PyVSwitch.Tests\PyVSwitch.Tests.csproj -c $Configuration --nologo -v q
    }

    Invoke-Step "Publish desktop app (pyvswitchw.exe)" {
        dotnet publish .\src\PyVSwitch.App\PyVSwitch.App.csproj `
            -c $Configuration -r $Runtime --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=none `
            -o $publishDir --nologo -v q
    }

    if ($NoAot) {
        Invoke-Step "Publish CLI (pyvswitch.exe, trimmed single file)" {
            dotnet publish .\src\PyVSwitch.Cli\PyVSwitch.Cli.csproj `
                -c $Configuration -r $Runtime --self-contained true `
                -p:PublishAot=false -p:PublishSingleFile=true -p:PublishTrimmed=true `
                -p:DebugType=none `
                -o $publishDir --nologo -v q
        }
    }
    else {
        Invoke-Step "Publish CLI (pyvswitch.exe, native AOT)" {
            dotnet publish .\src\PyVSwitch.Cli\PyVSwitch.Cli.csproj `
                -c $Configuration -r $Runtime `
                -p:DebugType=none `
                -o $publishDir --nologo -v q
        }
    }

    Get-ChildItem $publishDir -Filter *.pdb | Remove-Item -Force
    foreach ($exe in "pyvswitch.exe", "pyvswitchw.exe") {
        if (-not (Test-Path (Join-Path $publishDir $exe))) { throw "$exe was not produced." }
    }

    $msi = ".\artifacts\installer\pyvswitch-$version-win-x64.msi"
    $setup = ".\artifacts\installer\pyvswitch-setup-$version-win-x64.exe"

    Invoke-Step "Build MSI" {
        dotnet wix build .\installer\Package.wxs `
            -arch x64 -d Version=$version `
            -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
            -out $msi
    }

    Invoke-Step "Validate MSI" {
        dotnet wix msi validate $msi
    }

    Invoke-Step "Build setup EXE" {
        dotnet wix build .\installer\Bundle.wxs `
            -arch x64 -d Version=$version `
            -ext WixToolset.BootstrapperApplications.wixext `
            -out $setup
    }

    Write-Host ""
    Get-ChildItem $installerDir -File | Where-Object { $_.Name -like "*$version*" -and $_.Extension -in ".msi", ".exe" } |
        ForEach-Object { "{0,-46} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
}
finally {
    Pop-Location
}
