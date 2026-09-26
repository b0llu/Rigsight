# Checks a Release build (the folder the installer is made from): used by tools\test.ps1 and the Release workflow.
#   tools\check-release.ps1 -Publish <folder> [-DebugCore <Debug build's Rigsight.Core.dll>]
# 1. Both programs carry the version in Directory.Build.props.
# 2. It can't be pointed at a test update server: only Debug builds and test installers read RIGSIGHT_UPDATE_FEED
#    (the elevated agent must only ever trust GitHub). .NET keeps string literals as UTF-16, so that's what's looked
#    for; and the search is first proved on a Debug build, which has the name, so a broken check can't pass.
param([Parameter(Mandatory)][string]$Publish, [string]$DebugCore)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $DebugCore) { $DebugCore = Join-Path $root 'bin\Debug\Rigsight.Core.dll' }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
foreach ($exe in 'Rigsight.exe', 'Rigsight.Agent.exe') {
    $v = (Get-Item (Join-Path $Publish $exe)).VersionInfo.ProductVersion
    if (-not $v.StartsWith($version)) { throw "$exe is version $v, Directory.Build.props says $version" }
}
Write-Host "  version $version in both programs"

function Count-Utf16($file, $text) {
    $bytes = [IO.File]::ReadAllBytes($file)
    $needle = [Text.Encoding]::Unicode.GetBytes($text)
    $hex = [BitConverter]::ToString($bytes).Replace('-', '')
    ([regex]::Matches($hex, [BitConverter]::ToString($needle).Replace('-', ''))).Count
}
if (-not (Test-Path $DebugCore)) { throw "No Debug build of Rigsight.Core at $DebugCore to prove the update-feed check on" }
if ((Count-Utf16 $DebugCore 'RIGSIGHT_UPDATE_FEED') -lt 1) { throw "the update-feed check can't find the name even in a Debug build: the check is broken" }
if ((Count-Utf16 (Join-Path $Publish 'Rigsight.Core.dll') 'RIGSIGHT_UPDATE_FEED') -gt 0) { throw 'the Release build reads RIGSIGHT_UPDATE_FEED (built with RigsightTestFeed?)' }
Write-Host '  the Release build only trusts GitHub for updates'
