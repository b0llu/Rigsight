# Builds dist\Rigsight-Setup-<version>.exe: publishes both programs (self-contained, so the
# target PC needs no .NET) and packs them with Inno Setup.
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'

$iscc = @(
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($project in 'src\Rigsight.Agent', 'src\Rigsight') {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=none
    if ($LASTEXITCODE) { throw "dotnet publish $project failed" }
}

& $iscc "/DAppVersion=$version" "/DSourceDir=$publish" (Join-Path $root 'installer\Rigsight.iss')
if ($LASTEXITCODE) { throw 'Inno Setup failed' }

Write-Host "`nInstaller: $(Join-Path $root "dist\Rigsight-Setup-$version.exe")" -ForegroundColor Green

# The language packs, attached to the same release: dist\Rigsight-Language-<code>.json, made for this version. A pack
# with any text untranslated or not fitting stops the build, so a release never ships Spanish screens half in English.
Push-Location $root
try {
    foreach ($pack in Get-ChildItem (Join-Path $root 'languages\*.json')) {
        $code = $pack.BaseName
        dotnet run --project tools\Rigsight.LangTool -v q -- check $pack.FullName
        if ($LASTEXITCODE) { throw "The $code pack isn't complete (see the list above): run the language tool's sync, translate, and build again" }
        dotnet run --project tools\Rigsight.LangTool -v q -- release $pack.FullName $version (Join-Path $root "dist\Rigsight-Language-$code.json")
        if ($LASTEXITCODE) { throw "Writing the $code pack failed" }
    }
} finally { Pop-Location }
Write-Host "Attach dist\Rigsight-Language-*.json to the GitHub release too." -ForegroundColor Green
