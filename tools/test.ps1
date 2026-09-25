# Everything to run before a release. Stops at the first stage that fails.
#   powershell -ExecutionPolicy Bypass -File tools\test.ps1              # full run (about 15 min)
#   powershell -ExecutionPolicy Bypass -File tools\test.ps1 -Quick       # shorter end-to-end measures (about 8 min)
#   ... -SkipEndToEnd       unit and UI tests only
#   ... -UpdateBaseline     accept this run's end-to-end measures as the new baseline (only if it passed)
#   ... -Admin              the test copy's agent runs with admin rights, reading every sensor like an installed one
#                           (one Windows prompt to accept); measured against its own baseline
#   ... -Installed          also measure the installed Rigsight as it runs: its agent (read-only), and its app opened
#                           for about a minute and closed again
#   ... -InstalledBuild     also run the end-to-end check on the installed program files (0.5.13 or later)
# Before a release: tools\test.ps1 -Admin -Installed
#
# 1. Builds everything (Debug).
# 2. Unit, UI and performance tests (tests\Rigsight.Tests): logic, history and reports, the agent's parts, every page
#    of the app in both themes, and time/memory budgets.
# 3. Publishes the Release build exactly as the installer does, and checks it: version, and that it can't be pointed
#    at a test update server.
# 4. End-to-end (tests\Rigsight.E2E): that Release build runs as an isolated test copy on a year of generated history;
#    startup, CPU, memory, handles, leaks, clean shutdown and the log are checked against budgets and the baseline.
# Nothing here touches the installed Rigsight or its data: tests use their own folder, pipe and names.
param([switch]$Quick, [switch]$SkipEndToEnd, [switch]$UpdateBaseline, [switch]$Installed, [switch]$Admin, [switch]$InstalledBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = Join-Path $root "TestResults\$stamp"
New-Item -ItemType Directory -Force $results | Out-Null
# Only the last few runs are kept.
Get-ChildItem (Join-Path $root 'TestResults') -Directory | Where-Object Name -match '^\d{8}-\d{6}$' |
    Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$started = Get-Date

function Stage($name) { Write-Host "`n== $name" -ForegroundColor Cyan }
function Failed($message) {
    Write-Host "`nFAILED: $message" -ForegroundColor Red
    Write-Host "Results: $results"
    exit 1
}

Stage 'Build'
dotnet build (Join-Path $root 'Rigsight.slnx') -c Debug -nologo -v q
if ($LASTEXITCODE) { Failed 'build' }

Stage 'Unit, UI and performance tests'
dotnet test --project (Join-Path $root 'tests\Rigsight.Tests') --no-build --results-directory $results --report-xunit-trx
if ($LASTEXITCODE) { Failed 'tests (see above)' }

Stage 'Release build, as the installer makes it'
$publish = Join-Path $results 'publish'
foreach ($project in 'src\Rigsight.Agent', 'src\Rigsight') {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=none -nologo -v q
    if ($LASTEXITCODE) { Failed "dotnet publish $project" }
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
foreach ($exe in 'Rigsight.exe', 'Rigsight.Agent.exe') {
    $v = (Get-Item (Join-Path $publish $exe)).VersionInfo.ProductVersion
    if (-not $v.StartsWith($version)) { Failed "$exe is version $v, Directory.Build.props says $version" }
}
Write-Host "  version $version in both programs"

# Only Debug builds and test installers may read RIGSIGHT_UPDATE_FEED (the elevated agent must only ever trust GitHub).
# .NET keeps string literals as UTF-16, so look for that; and prove the search works on a Debug build, which has it.
function Count-Utf16($file, $text) {
    $bytes = [IO.File]::ReadAllBytes($file)
    $needle = [Text.Encoding]::Unicode.GetBytes($text)
    $hex = [BitConverter]::ToString($bytes).Replace('-', '')
    ([regex]::Matches($hex, [BitConverter]::ToString($needle).Replace('-', ''))).Count
}
$debugCore = Join-Path $root 'bin\Debug\Rigsight.Core.dll'
if ((Count-Utf16 $debugCore 'RIGSIGHT_UPDATE_FEED') -lt 1) { Failed "the update-feed check can't find the name even in a Debug build: the check is broken" }
if ((Count-Utf16 (Join-Path $publish 'Rigsight.Core.dll') 'RIGSIGHT_UPDATE_FEED') -gt 0) { Failed 'the Release build reads RIGSIGHT_UPDATE_FEED (built with RigsightTestFeed?)' }
Write-Host '  the Release build only trusts GitHub for updates'

if (-not $SkipEndToEnd) {
    Stage 'End-to-end: the Release build as an isolated test copy'
    $e2eArgs = @('--bin', $publish, '--out', (Join-Path $results 'e2e'))
    if ($Quick) { $e2eArgs += '--quick' }
    if ($UpdateBaseline) { $e2eArgs += '--update-baseline' }
    if ($Installed) { $e2eArgs += '--installed' }
    if ($Admin) { $e2eArgs += '--admin'; Write-Host '  (a Windows admin prompt will appear for the test agent: accept it)' -ForegroundColor Yellow }
    dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release -- @e2eArgs
    if ($LASTEXITCODE) { Failed 'end-to-end (see the report in the results folder)' }

    if ($InstalledBuild) {
        Stage 'End-to-end: the installed program files as an isolated test copy'
        $installedArgs = @('--bin', (Join-Path $env:ProgramFiles 'Rigsight'), '--out', (Join-Path $results 'e2e-installed'))
        if ($Quick) { $installedArgs += '--quick' }
        if ($Admin) { $installedArgs += '--admin' }
        dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release --no-build -- @installedArgs
        if ($LASTEXITCODE) { Failed 'end-to-end on the installed build' }
    }
}

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "`nALL PASSED in $([int]((Get-Date) - $started).TotalMinutes) min. Results: $results" -ForegroundColor Green
