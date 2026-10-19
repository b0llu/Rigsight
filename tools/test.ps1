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
#   -Quick              (kept for old habits: the end-to-end run is always the short one now)
#   -Year               also the end-to-end run on a year of made-up history. Without it that run is only made when
#                       how history is stored, read or summed up changed since the last release (src\Rigsight.Core\Data
#                       and Reports, the app's ReportService): that is what a long history can slow down
#   -SkipEndToEnd       unit and UI tests only
#   -UpdateBaseline     accept this run's end-to-end measures (only if it passed): added to the baseline, which is the
#                       last five accepted runs; a later run is compared with the middle value of each measure.
#                       The made-up year's run has a baseline of its own and is added to it the same way
#   -NewBaseline        as -UpdateBaseline, but the baseline starts over from this run alone: after a change that was
#                       meant to cost what it now costs (the middle of the earlier runs would go on failing it)
#   -Admin              the test copy's agent runs with admin rights, reading every sensor like an installed one;
#                       measured against its own baseline. One Windows prompt, at the start, for tools\admin-helper.ps1,
#                       which then starts the test agent with admin rights for every run until one passes (or 2 hours
#                       go by without a run): a run after a failure asks nothing
#   -Installed          also measure the installed Rigsight as it runs: its agent (read-only), and its app opened
#                       for about half a minute and closed again
#   -InstalledBuild     also run the end-to-end check on the installed program files (0.5.13 or later)
#   -Parts <n>          how many test processes run side by side (5); 1 = one after another, as it used to be
#
# The unit and UI tests run as several processes side by side (tests\Rigsight.Impact --split deals the classes into
# parts of about the same length, going by the last run's times), then the classes marked [Trait("Alone", ...)] on
# their own: the ones that measure time or memory, which other work on the PC would skew, and the ones that use the
# clipboard. The end-to-end runs measure too, so they stay one after another, with nothing beside them.
#
# Stages: build (Debug) -> which tests (unless -Full) -> tests, while the Release build is made exactly as the
# installer makes it -> Release checks (version; it can't be pointed at a test update server) -> end-to-end: that
# Release build runs as an isolated test copy on a copy of this PC's own history (-Seeded: a made-up year; a full gate
# also runs that one, quick) (startup, CPU, memory, handles, leaks,
# clean shutdown, the log; against budgets and the baseline). Nothing here touches the installed Rigsight; its history is only read, to copy it.
param([switch]$Full, [string]$Base, [switch]$Quick, [switch]$SkipEndToEnd, [switch]$UpdateBaseline, [switch]$NewBaseline, [switch]$Installed, [switch]$Admin, [switch]$InstalledBuild, [switch]$Seeded, [switch]$Year, [int]$Parts = 5)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# From the repository, wherever it's started from: dotnet reads global.json (the test runner) from the current folder.
Set-Location $root
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

# -Admin: the admin helper, asked for once (a Windows prompt now, while you're here) and kept for the runs that follow
# until one passes.
$adminDir = Join-Path $env:LOCALAPPDATA 'RigsightTestAdmin'
function HelperAlive {
    $alive = Join-Path $adminDir 'alive'
    (Test-Path $alive) -and ((Get-Date) - (Get-Item $alive).LastWriteTime).TotalSeconds -lt 10
}
if ($Admin -and -not $SkipEndToEnd) {
    if (HelperAlive) { Write-Host '  Admin helper already running: no Windows prompt this time.' -ForegroundColor DarkGray }
    else {
        Write-Host '  A Windows admin prompt now, once: accept it and no run after this asks again until one passes.' -ForegroundColor Yellow
        try {
            Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                "`"$(Join-Path $PSScriptRoot 'admin-helper.ps1')`"", '-Root', "`"$root`"", '-Temp', "`"$([IO.Path]::GetTempPath().TrimEnd('\'))`""
        }
        catch { Failed 'the Windows admin prompt was declined' }
        $wait = [Diagnostics.Stopwatch]::StartNew()
        while (-not (HelperAlive) -and $wait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
        if (-not (HelperAlive)) { Failed 'the admin helper did not start' }
    }
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
$splitJson = Join-Path $results 'split.json'
$tests = Join-Path $root 'tests\Rigsight.Tests'

# One test process for these classes, its results and its output in a folder of its own.
function TestArgs($name, $classes) {
    $list = @('test', '--project', "`"$tests`"", '--no-build', '--results-directory', "`"$(Join-Path $results $name)`"", '--report-xunit-trx')
    foreach ($c in $classes) { $list += @('--filter-class', $c) }
    $list
}
function TestsIn($log) { if ((Get-Content $log -Raw) -match '(?m)^\s*total:\s*(\d+)') { [int]$Matches[1] } else { 0 } }

if (-not $Full) {
    Stage 'Which tests the changes need'
    $impactJson = Join-Path $results 'impact.json'
    $impactArgs = @('--json', $impactJson, '--split', $splitJson, '--parts', $Parts)
    if ($Base) { $impactArgs += @('--base', $Base) }
    dotnet run --project (Join-Path $root 'tests\Rigsight.Impact') -c Release -- @impactArgs
    if ($LASTEXITCODE) { Failed 'working out which tests to run' }
    $impact = Get-Content $impactJson -Raw | ConvertFrom-Json
    $level = $impact.Level; $classes = @($impact.Classes); $e2eMode = $impact.EndToEnd
}

if ($Full) {
    dotnet run --project (Join-Path $root 'tests\Rigsight.Impact') -c Release -- --all --split $splitJson --parts $Parts
    if ($LASTEXITCODE) { Failed 'dealing the tests into parts' }
}
$split = Get-Content $splitJson -Raw | ConvertFrom-Json
$partCount = @($split.Parts).Count
$ran = 0
$what = if ($level -eq 'full') { 'all' } else { "$($classes.Count) test class(es) the changes reach" }
if ($partCount -gt 0 -and @($split.Parts[0]).Count -gt 0) {
    Stage "Unit and UI tests: $what, as $partCount process(es) side by side"
    $running = for ($i = 0; $i -lt $partCount; $i++) {
        $name = "part$($i + 1)"
        New-Item -ItemType Directory -Force (Join-Path $results $name) | Out-Null
        $log = Join-Path $results "$name\output.log"
        $process = Start-Process dotnet -ArgumentList (TestArgs $name @($split.Parts[$i])) -WorkingDirectory $root -NoNewWindow -PassThru `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        # Its handle is taken now: Windows PowerShell has no exit code to give for a process that ended before it looked.
        $null = $process.Handle
        [pscustomobject]@{ Name = $name; Log = $log; Classes = @($split.Parts[$i]).Count; Process = $process }
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $bad = @()
    foreach ($part in $running) {
        $part.Process.WaitForExit()
        $count = TestsIn $part.Log
        $ran += $count
        $ok = $part.Process.ExitCode -eq 0
        Write-Host ("  {0}: {1} classes, {2} tests, {3} (done at {4:0} s)" -f $part.Name, $part.Classes, $count, $(if ($ok) { 'passed' } else { 'FAILED' }), $clock.Elapsed.TotalSeconds) -ForegroundColor $(if ($ok) { 'Gray' } else { 'Red' })
        if (-not $ok) { $bad += $part }
    }
    foreach ($part in $bad) {
        Write-Host "`n-- $($part.Name): $($part.Log)" -ForegroundColor Red
        Get-Content $part.Log | Where-Object { $_ -notmatch '^\s*$' } | Select-Object -Last 120
        Get-Content "$($part.Log).err" -ErrorAction SilentlyContinue | Select-Object -Last 20
    }
    if ($bad.Count) { Failed "tests in $(($bad | ForEach-Object Name) -join ', ') (see above)" }
}
if (@($split.Alone).Count -gt 0) {
    Stage "Tests that measure time or memory, or use the clipboard: $(@($split.Alone).Count) class(es), on their own"
    $log = Join-Path $results 'alone-output.log'
    $list = TestArgs 'alone' @($split.Alone)
    & dotnet @($list | ForEach-Object { $_.Trim('"') }) | Tee-Object -FilePath $log
    if ($LASTEXITCODE) { Failed 'tests (see above)' }
    $ran += TestsIn $log
}
Write-Host "  $ran tests in all" -ForegroundColor DarkGray

Stage 'Release build, as the installer makes it'
$job = Get-Job -Name ReleaseBuild
Wait-Job $job | Out-Null
if ($job.State -ne 'Completed') { $err = ($job.ChildJobs[0].JobStateInfo.Reason | Out-String); Remove-Job $job -Force; Failed "release build: $err" }
Remove-Job $job

try { & (Join-Path $PSScriptRoot 'check-release.ps1') -Publish $publish }
catch { Failed "release checks: $($_.Exception.Message)" }

if ($SkipEndToEnd -or $e2eMode -eq 'none') {
    if ($e2eMode -eq 'none' -and -not $SkipEndToEnd) { Write-Host "`n  (no end-to-end run: the programs didn't change)" -ForegroundColor DarkGray }
}
else {
    $short = $Quick -or $e2eMode -eq 'quick'
    Stage "End-to-end: the Release build as an isolated test copy$(if ($short) { ' (quick)' })"
    $e2eArgs = @('--bin', $publish, '--out', (Join-Path $results 'e2e'))
    # On a copy of this PC's own history unless -Seeded: what the pages really hold is what has to be quick.
    if (-not $Seeded) { $e2eArgs += '--real-data' }
    if ($short) { $e2eArgs += '--quick' }
    $baselineArg = if ($NewBaseline) { '--new-baseline' } elseif ($UpdateBaseline) { '--update-baseline' }
    if ($baselineArg) { $e2eArgs += $baselineArg }
    if ($Installed) { $e2eArgs += '--installed' }
    if ($Admin) { $e2eArgs += '--admin'; Write-Host '  (a Windows admin prompt will appear for the test agent: accept it)' -ForegroundColor Yellow }
    dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release -- @e2eArgs
    if ($LASTEXITCODE) { Failed 'end-to-end (see the report in the results folder)' }

    # Each language pack, as people download it: the same page switches, CPU and memory against the same budgets as
    # English (a translated line is looked up and filled each time; pages, widgets and the overlay redraw every second).
    foreach ($pack in Get-ChildItem (Join-Path $root 'languages\*.json') -ErrorAction SilentlyContinue) {
        $code = $pack.BaseName
        Stage "End-to-end in $code (quick): the same budgets as English"
        $released = Join-Path $results "language-$code.json"
        $version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
        Push-Location $root
        dotnet run --project tools\Rigsight.LangTool -v q -- release $pack.FullName $version $released
        $made = $LASTEXITCODE
        Pop-Location
        if ($made) { Failed "the $code pack (see the language tool's output above)" }
        $langArgs = @('--bin', $publish, '--out', (Join-Path $results "e2e-$code"), '--quick', '--language', $released)
        if (-not $Seeded) { $langArgs += '--real-data' }
        if ($Admin) { $langArgs += '--admin' }
        dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release --no-build -- @langArgs
        if ($LASTEXITCODE) { Failed "end-to-end in $code (see the report in the results folder)" }
    }

    # The made-up year: what a long history costs, which this PC's own doesn't show yet. Only when asked for (-Year)
    # or when the code that stores, reads or sums up history changed since the last release.
    $history = $false
    if (-not $Year) {
        $tag = git -C $root describe --tags --abbrev=0 2>$null
        $history = [bool]($tag -and (git -C $root diff --name-only $tag -- src/Rigsight.Core/Data src/Rigsight.Core/Reports src/Rigsight/Services/ReportService.cs))
    }
    if (-not $Seeded -and ($Year -or $history)) {
        Stage "End-to-end: a year of made-up history$(if (-not $Year) { ' (the history code changed since the last release)' })"
        $yearArgs = @('--bin', $publish, '--out', (Join-Path $results 'e2e-year'), '--quick')
        if ($Admin) { $yearArgs += '--admin' }
        if ($baselineArg) { $yearArgs += $baselineArg }
        dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release --no-build -- @yearArgs
        if ($LASTEXITCODE) { Failed 'end-to-end on a year of history' }
    }

    if ($InstalledBuild) {
        Stage 'End-to-end: the installed program files as an isolated test copy'
        $installedArgs = @('--bin', (Join-Path $env:ProgramFiles 'Rigsight'), '--out', (Join-Path $results 'e2e-installed'), '--quick')
        if ($Admin) { $installedArgs += '--admin' }
        dotnet run --project (Join-Path $root 'tests\Rigsight.E2E') -c Release --no-build -- @installedArgs
        if ($LASTEXITCODE) { Failed 'end-to-end on the installed build' }
    }
}

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
# Passed: the admin helper's job is done.
if ($Admin -and (HelperAlive)) { Set-Content (Join-Path $adminDir 'stop') 'passed' }
Write-Host "`nALL PASSED ($level) in $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min. Results: $results" -ForegroundColor Green
