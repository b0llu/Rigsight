# Rigsight installer
#
#   irm https://b0llu.github.io/Rigsight/install.ps1 | iex
#
# Downloads the latest Rigsight release from GitHub, checks it against the checksum GitHub publishes for it,
# and opens its setup. Run it again at any time to update: it always opens the newest version's setup.
# Works in Windows PowerShell 5.1 and PowerShell 7.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads many times slower in PowerShell 5.1
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo = 'b0llu/Rigsight'
    $appId = '{5AAE562A-D0E3-4CCA-82CA-6CA35DD0FF7E}_is1'

    function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

    Say ''
    Say '  Rigsight' 'White'
    Say '  Hardware and usage metrics for Windows' 'DarkGray'
    Say ''

    # The installed version, if any (the setup registers itself like any Windows program).
    $installed = $null
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall') {
        $key = Get-ItemProperty -Path "$root\$appId" -ErrorAction SilentlyContinue
        if ($key -and $key.DisplayVersion) { $installed = $key.DisplayVersion; break }
    }

    Say '  Looking up the latest version...'
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'Rigsight-installer'; 'Accept' = 'application/vnd.github+json' }
    }
    catch {
        Say "  Couldn't reach GitHub: $($_.Exception.Message)" 'Red'
        Say "  You can download the setup from https://github.com/$repo/releases/latest instead." 'Yellow'
        return
    }
    $asset = $release.assets | Where-Object { $_.name -like '*.exe' } | Select-Object -First 1
    if (-not $asset) { Say "  The latest release has no setup file yet. See https://github.com/$repo/releases" 'Red'; return }
    $latest = $release.tag_name.TrimStart('v')

    if ($installed -eq $latest) { Say "  Rigsight $latest is installed, and it's the latest. Opening its setup to repair or reinstall." 'Green' }
    elseif ($installed) { Say "  Rigsight $installed is installed. Updating to $latest." 'Green' }
    else { Say "  Installing Rigsight $latest." 'Green' }

    $file = Join-Path $env:TEMP $asset.name
    Say ("  Downloading {0} ({1:N0} MB)..." -f $asset.name, ($asset.size / 1MB))
    try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing }
    catch { Say "  Download failed: $($_.Exception.Message)" 'Red'; return }

    # Make sure it's exactly the file GitHub has for this release.
    if ((Get-Item $file).Length -ne $asset.size) { Remove-Item $file -Force; Say '  The download is incomplete. Please run the command again.' 'Red'; return }
    if ($asset.digest -and $asset.digest -like 'sha256:*') {
        $expected = $asset.digest.Substring(7)
        $actual = (Get-FileHash -Path $file -Algorithm SHA256).Hash
        if ($actual -ne $expected) { Remove-Item $file -Force; Say "  The download doesn't match the release's checksum, so it wasn't opened. Please try again." 'Red'; return }
        Say '  Checksum verified.' 'DarkGray'
    }

    if ($env:RIGSIGHT_INSTALL_DOWNLOAD_ONLY) { Say "  Downloaded to $file (not opened)." 'Yellow'; return }

    Say '  Opening the setup. Windows will ask for permission to install.'
    Start-Process -FilePath $file
    Say ''
}
