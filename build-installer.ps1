param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    $publishDir = Join-Path $root "artifacts\publish\$Runtime"
    $installerDir = Join-Path $root "artifacts\installer"
    New-Item -ItemType Directory -Force -Path $publishDir, $installerDir | Out-Null

    dotnet tool restore
    dotnet wix extension add WixToolset.UI.wixext/5.0.2
    dotnet wix extension add WixToolset.BootstrapperApplications.wixext/5.0.2

    dotnet publish .\PythonVersionSwitch.csproj `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir

    dotnet wix build .\installer\Package.wxs `
        -arch x64 `
        -ext WixToolset.UI.wixext `
        -out .\artifacts\installer\pyvswitch-0.4.0-win-x64.msi

    dotnet wix msi validate .\artifacts\installer\pyvswitch-0.4.0-win-x64.msi

    dotnet wix build .\installer\Bundle.wxs `
        -arch x64 `
        -ext WixToolset.BootstrapperApplications.wixext `
        -out .\artifacts\installer\pyvswitch-setup-0.4.0-win-x64.exe
}
finally {
    Pop-Location
}
