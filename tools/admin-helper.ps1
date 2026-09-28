# The admin helper for test runs: started with admin rights once (one Windows prompt) by tools\test.ps1 -Admin, it
# starts the end-to-end check's test agent with admin rights for every run after, without asking again, until a run
# passes (test.ps1 then stops it) or nobody has asked for 2 hours.
#
# It does one thing only: start Rigsight.Agent.exe from this repo's test results (or the installed Rigsight), with
# a data folder in the end-to-end check's temp folder. Anything else asked of it is refused. Requests and answers are
# files in %LOCALAPPDATA%\RigsightTestAdmin (see tests\Rigsight.E2E, StartElevated).
param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Temp, [int]$IdleMinutes = 120)

$dir = Join-Path $env:LOCALAPPDATA 'RigsightTestAdmin'
$results = [IO.Path]::GetFullPath((Join-Path $Root 'TestResults')).TrimEnd('\') + '\'
$installed = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Rigsight')).TrimEnd('\') + '\'
$dataRoot = [IO.Path]::GetFullPath((Join-Path $Temp 'rigsight-e2e')).TrimEnd('\') + '\'
New-Item -ItemType Directory -Force $dir | Out-Null
Get-ChildItem $dir -File | Remove-Item -Force -ErrorAction SilentlyContinue

function Under([string]$path, [string]$folder) { $path.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase) }

$lastAsked = Get-Date
try {
    while ($true) {
        # "Still here" for test.ps1 and the end-to-end check (they only use a helper that wrote this lately).
        Set-Content (Join-Path $dir 'alive') "$PID $((Get-Date).ToUniversalTime().ToString('o'))"
        if (Test-Path (Join-Path $dir 'stop')) { break }
        if (((Get-Date) - $lastAsked).TotalMinutes -ge $IdleMinutes) { break }

        foreach ($request in Get-ChildItem $dir -Filter 'request-*.json' -File) {
            $lastAsked = Get-Date
            $id = $request.BaseName.Substring('request-'.Length)
            try {
                $ask = Get-Content $request.FullName -Raw | ConvertFrom-Json
                $exe = [IO.Path]::GetFullPath($ask.Exe)
                $data = [IO.Path]::GetFullPath($ask.Data)
                if ((Split-Path $exe -Leaf) -ne 'Rigsight.Agent.exe' -or -not ((Under $exe $results) -or (Under $exe $installed))) { throw "not a Rigsight test agent: $exe" }
                if (-not (Under $data $dataRoot)) { throw "not an end-to-end data folder: $data" }
                $agent = Start-Process $exe -ArgumentList '--data-dir', "`"$data`"" -WorkingDirectory (Split-Path $exe) -PassThru
                Set-Content (Join-Path $dir "response-$id.txt") $agent.Id
            }
            catch { Set-Content (Join-Path $dir "response-$id.txt") "error: $($_.Exception.Message)" }
            Remove-Item $request.FullName -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 300
    }
}
finally {
    Remove-Item (Join-Path $dir 'alive'), (Join-Path $dir 'stop') -Force -ErrorAction SilentlyContinue
}
