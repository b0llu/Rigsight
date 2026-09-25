# The checks before a release. Stops at the first stage that fails.
#   powershell -ExecutionPolicy Bypass -File tools\test.ps1 -Admin -Installed     # before a release
#
# By default only what the changes since the last release need is run (tests\Rigsight.Impact decides, and says why):
#   none      nothing the programs use changed: build, the release build checks, and any changed tests
#   selected  the test classes that reach the changed code, every page rendered if anything on screen changed, and a
#             quick end-to-end run if the programs changed
#   full      something shared or hard to follow changed (settings, the pipe protocol, history storage, themes, the
#             agent's main loop, startup, build files, test helpers): everything
#   -Full               everything, whatever changed
#   -Base <git ref>     compare with this instead of the last release tag
#   -Quick              shorter end-to-end measures
#   -SkipEndToEnd       unit and UI tests only
#   -UpdateBaseline     accept this run's end-to-end measures as the new baseline (only if it passed)
#   -Admin              the test copy's agent runs with admin rights, reading every sensor like an installed one
#                       (one Windows prompt to accept); measured against its own baseline
#   -Installed          also measure the installed Rigsight as it runs: its agent (read-only), and its app opened
#                       for about half a minute and closed again
#   -InstalledBuild     also run the end-to-end check on the installed program files (0.5.13 or later)
#
# Stages: build (Debug) -> which tests (unless -Full) -> tests, while the Release build is made exactly as the
# installer makes it -> Release checks (version; it can't be pointed at a test update server) -> end-to-end: that
# Release build runs as an isolated test copy on a year of generated history (startup, CPU, memory, handles, leaks,
# clean shutdown, the log; against budgets and the baseline). Nothing here touches the installed Rigsight or its data.
param([switch]$Full, [string]$Base, [switch]$Quick, [switch]$SkipEndToEnd, [switch]$UpdateBaseline, [switch]$Installed, [switch]$Admin, [switch]$InstalledBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = Join-Path $root "TestResults\$stamp"
New-Item -ItemType Directory -Force $results | Out-Null
# Only the last few runs are kept.
Get-ChildItem (Join-Path $root 'TestResults') -Directory | Where-Object Name -match '^\d{8}-\d{6}$' |
    Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$started = Get-Date
$publish = Join-Path $results 'publish'

function Stage($name) { Write-Host "`n== $name" -ForegroundColor Cyan }
function Failed($message) {
    Get-Job -Name ReleaseBuild -ErrorAction SilentlyContinue | Remove-Job -Force
    Write-Host "`nFAILED: $message" -ForegroundColor Red
    Write-Host "Results: $results"
    exit 1
}

Stage 'Build'
dotnet build (Join-Path $root 'Rigsight.slnx') -c Debug -nologo -v q
if ($LASTEXITCODE) { Failed 'build' }

# The Release build, meanwhile (it doesn't share any output with the Debug tests).
Start-Job -Name ReleaseBuild -ArgumentList $root, $publish -ScriptBlock {
    param($root, $publish)
    foreach ($project in 'src\Rigsight.Agent', 'src\Rigsight') {
        $out = dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=none -nologo -v q 2>&1
        if ($LASTEXITCODE) { throw "dotnet publish $project failed:`n$($out -join "`n")" }
    }
} | Out-Null

$level = 'full'; $classes = @(); $e2eMode = 'full'
if (-not $Full) {
    Stage 'Which tests the changes need'
    $impactJson = Join-Path $results 'impact.json'
    $impactArgs = @('--json', $impactJson)
    if ($Base) { $impactArgs += @('--base', $Base) }
    dotnet run --project (Join-Path $root 'tests\Rigsight.Impact') -c Release -- @impactArgs
    if ($LASTEXITCODE) { Failed 'working out which tests to run' }
    $impact = Get-Content $impactJson -Raw | ConvertFrom-Json
    $level = $impact.Level; $classes = @($impact.Classes); $e2eMode = $impact.EndToEnd
}

if ($level -eq 'full') {
    Stage 'Unit, UI and performance tests: all'
    dotnet test --project (Join-Path $root 'tests\Rigsight.Tests') --no-build --results-directory $results --report-xunit-trx
}
else {
    Stage "Unit, UI and performance tests: $($classes.Count) test class(es) the changes reach"
    $filters = foreach ($c in $classes) { '--filter-class'; $c }
    dotnet test --project (Join-Path $root 'tests\Rigsight.Tests') --no-build --results-directory $results --report-xunit-trx @filters
}
if ($LASTEXITCODE) { Failed 'tests (see above)' }

Stage 'Release build, as the installer makes it'
$job = Get-Job -Name ReleaseBuild
Wait-Job $job | Out-Null
if ($job.State -ne 'Completed') { $err = ($job.ChildJobs[0].JobStateInfo.Reason | Out-String); Remove-Job $job -Force; Failed "release build: $err" }
Remove-Job $job

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

if ($SkipEndToEnd -or $e2eMode -eq 'none') {
    if ($e2eMode -eq 'none' -and -not $SkipEndToEnd) { Write-Host "`n  (no end-to-end run: the programs didn't change)" -ForegroundColor DarkGray }
}
else {
    $short = $Quick -or $e2eMode -eq 'quick'
    Stage "End-to-end: the Release build as an isolated test copy$(if ($short) { ' (quick)' })"
    $e2eArgs = @('--bin', $publish, '--out', (Join-Path $results 'e2e'))
    if ($short) { $e2eArgs += '--quick' }
    if ($UpdateBaseline) { $e2eArgs += '--update-baseline' }
    if ($Installed) { $e2eArgs += '--installed' }
    if ($Admin) { $e2eArgs += '--admin'; Write-Host '  (a Windows admin prompt will appear for the test agent: accept it)' -ForegroundColor Yellow }
    dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release -- @e2eArgs
    if ($LASTEXITCODE) { Failed 'end-to-end (see the report in the results folder)' }

    if ($InstalledBuild) {
        Stage 'End-to-end: the installed program files as an isolated test copy'
        $installedArgs = @('--bin', (Join-Path $env:ProgramFiles 'Rigsight'), '--out', (Join-Path $results 'e2e-installed'), '--quick')
        if ($Admin) { $installedArgs += '--admin' }
        dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release --no-build -- @installedArgs
        if ($LASTEXITCODE) { Failed 'end-to-end on the installed build' }
    }
}

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "`nALL PASSED ($level) in $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min. Results: $results" -ForegroundColor Green
