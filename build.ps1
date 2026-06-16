<#
.SYNOPSIS
    Builds MySqlModule and assembles the deployable TCAdmin package in dist\.

.DESCRIPTION
    Run this after ANY source change so dist\ stays in sync with the code.
    dist\ is gitignored and is the artifact you actually deploy to a TCAdmin
    server, so a stale dist is the same as an un-shipped fix.

    The csproj's PackageModule target (AfterTargets=Build) writes dist\, so a
    successful build always refreshes the whole package. See BUILD.md for the
    full prerequisites and the deploy steps.

.PARAMETER Configuration
    Release (default) or Debug.

.EXAMPLE
    .\build.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

# 1. Locate MSBuild via vswhere (works for full VS or the Build Tools).
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio 2022 or the VS Build Tools with the MSBuild component. See BUILD.md."
}
$msbuild = & $vswhere -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild not found. Add the 'MSBuild' / '.NET desktop build tools' component in the VS Installer. See BUILD.md."
}
Write-Host "MSBuild              : $msbuild"

# 2. Resolve the .NET Framework 4.7.2 reference assemblies.
#    Preferred: the 4.7.2 Developer Pack is installed -> nothing extra needed.
#    Fallback : point FrameworkPathOverride at extracted reference assemblies
#               (set $env:NET472_REF_DIR, else the default extract location).
$targetingPack = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\mscorlib.dll"
$extraArgs = @()
if (Test-Path $targetingPack) {
    Write-Host "Reference assemblies : 4.7.2 Developer Pack (installed)"
}
else {
    $fwo = if ($env:NET472_REF_DIR) { $env:NET472_REF_DIR } else { Join-Path $env:TEMP 'refasm472\build\.NETFramework\v4.7.2' }
    if (-not (Test-Path (Join-Path $fwo 'mscorlib.dll'))) {
        throw @"
.NET Framework 4.7.2 reference assemblies not found.
Fix it one of two ways (see BUILD.md > Prerequisites):
  * Install the '.NET Framework 4.7.2 Developer Pack', OR
  * Extract the Microsoft.NETFramework.ReferenceAssemblies.net472 NuGet package
    and set `$env:NET472_REF_DIR to its build\.NETFramework\v4.7.2 folder.
Looked in: $fwo
"@
    }
    Write-Host "FrameworkPathOverride: $fwo"
    $extraArgs = @("/p:FrameworkPathOverride=$fwo")
}

# 3. Sanity-check the gitignored package restore (the TCAdmin SDK is vendored).
if (-not (Test-Path (Join-Path $root 'packages\TCAdmin.2.0.149.5'))) {
    throw "packages\TCAdmin.2.0.149.5 is missing (packages\ is gitignored). See BUILD.md > Restore packages."
}

# 4. Build. PackageModule (AfterTargets=Build) assembles dist\.
& $msbuild 'MySqlModule.csproj' /t:Rebuild "/p:Configuration=$Configuration" /nologo /v:minimal @extraArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

# 5. Confirm the package was produced.
$dll = Join-Path $root 'dist\MySqlModule.dll'
if (-not (Test-Path $dll)) { throw "Build succeeded but dist\MySqlModule.dll is missing - check the PackageModule target." }
$info = Get-Item $dll

# 6. Zip the package into mysqlmanager.zip at the repo root. The archive holds the
#    dist\ CONTENTS at its root (MySqlModule.dll, Monitor\, Module.json, *.sql,
#    Views\) so it extracts straight into the module folder with no "dist" wrapper.
$zip = Join-Path $root 'mysqlmanager.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $root 'dist\*') -DestinationPath $zip -CompressionLevel Optimal
$zipInfo = Get-Item $zip

# 7. Report.
Write-Host ''
Write-Host "Build OK ($Configuration)." -ForegroundColor Green
Write-Host "Package : $(Join-Path $root 'dist')"
Write-Host ("Module  : {0}  ({1:N0} bytes, built {2:yyyy-MM-dd HH:mm})" -f $dll, $info.Length, $info.LastWriteTime)
Write-Host ("Zip     : {0}  ({1:N0} bytes)" -f $zip, $zipInfo.Length) -ForegroundColor Green
Write-Host 'Deploy: extract mysqlmanager.zip onto the TCAdmin server (or copy dist\ contents). See BUILD.md > Deploy.'
