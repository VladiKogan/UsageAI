# Installs or updates UsageAI for Windows from the latest GitHub release:
#
#   irm https://raw.githubusercontent.com/VladiKogan/UsageAI/main/scripts/install.ps1 | iex
#
# It downloads the release's Setup.exe, checks it against the SHA-256 published beside it, runs
# Setup silently (Windows asks for administrator approval), and starts UsageAI. Running it again
# updates to the newest release. Works in Windows PowerShell 5.1 and PowerShell 7.
#
# Run as a file, `-DownloadOnly` stops after the download and checksum, without installing.

param(
    [switch]$DownloadOnly
)

function Install-UsageAI {
    param([switch]$DownloadOnly)

    $ErrorActionPreference = 'Stop'
    $repository = 'VladiKogan/UsageAI'
    $appId = '{B1C6D9E4-6F5B-4E7B-9C2A-6E7B7F6B9B1D}_is1'

    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
        throw 'UsageAI needs 64-bit Windows 10 or 11.'
    }

    # Windows PowerShell 5.1 may still default to TLS 1.0, which GitHub refuses.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'UsageAI-install-script'; 'Accept' = 'application/vnd.github+json' }

    Write-Host 'Finding the latest UsageAI release...'
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers $headers
    $setup = $release.assets | Where-Object { $_.name -match '^UsageAI-[0-9][0-9A-Za-z.+-]*-Setup\.exe$' } | Select-Object -First 1
    if (-not $setup) {
        throw "Release $($release.tag_name) has no Setup.exe. Download UsageAI from https://github.com/$repository/releases instead."
    }

    $checksum = $release.assets | Where-Object { $_.name -eq "$($setup.name).sha256" } | Select-Object -First 1
    if (-not $checksum) {
        throw "Release $($release.tag_name) has no checksum for $($setup.name), so it is not installed."
    }

    # Only files from this repository's own releases are downloaded.
    $prefix = "https://github.com/$repository/releases/download/"
    foreach ($url in @($setup.browser_download_url, $checksum.browser_download_url)) {
        if (-not $url.StartsWith($prefix, [StringComparison]::Ordinal)) {
            throw "Unexpected download address: $url"
        }
    }

    $folder = Join-Path ([IO.Path]::GetTempPath()) ("UsageAI-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    $installer = Join-Path $folder $setup.name
    $progress = $ProgressPreference
    try {
        # The progress bar slows Invoke-WebRequest down many times over in Windows PowerShell.
        $ProgressPreference = 'SilentlyContinue'
        Write-Host "Downloading $($setup.name)..."
        Invoke-WebRequest -Uri $setup.browser_download_url -OutFile $installer -UseBasicParsing -Headers $headers
        $expected = ((Invoke-RestMethod -Uri $checksum.browser_download_url -Headers $headers) -split '\s+' |
            Where-Object { $_ -match '^[0-9A-Fa-f]{64}$' } | Select-Object -First 1)
        if (-not $expected) {
            throw "The checksum file for $($setup.name) holds no SHA-256 value."
        }

        $actual = (Get-FileHash -Path $installer -Algorithm SHA256).Hash
        if ($actual -ne $expected.ToUpperInvariant()) {
            throw "$($setup.name) does not match its published SHA-256, so it was not run."
        }

        Write-Host 'Checksum verified.'
        if ($DownloadOnly) {
            Write-Host "Downloaded to $installer (not installed)."
            return
        }

        Write-Host "Installing UsageAI $($release.tag_name.TrimStart('v')). Approve the Windows prompt to continue..."
        $process = Start-Process -FilePath $installer -Wait -PassThru -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CLOSEAPPLICATIONS', '/RESTARTAPPLICATIONS')
        if ($process.ExitCode -ne 0) {
            throw "Setup stopped with exit code $($process.ExitCode). UsageAI was not installed or updated."
        }

        # A silent install does not start the app; start it unless Setup already restarted it.
        $location = $null
        foreach ($root in @('HKLM:', 'HKCU:')) {
            foreach ($view in @('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
                $key = Join-Path "$root\$view" $appId
                if (-not $location -and (Test-Path $key)) {
                    $location = (Get-ItemProperty -Path $key).InstallLocation
                }
            }
        }

        $exe = if ($location) { Join-Path $location 'UsageAI.exe' } else { $null }
        if ($exe -and (Test-Path $exe)) {
            if (-not (Get-Process -Name 'UsageAI' -ErrorAction SilentlyContinue)) {
                Start-Process -FilePath $exe
            }

            Write-Host 'UsageAI is installed and running in the tray. Click its icon to open it.'
        }
        else {
            Write-Host 'UsageAI is installed. Start it from the Start menu.'
        }
    }
    finally {
        $ProgressPreference = $progress
        if (-not $DownloadOnly) {
            Remove-Item -Path $folder -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Install-UsageAI -DownloadOnly:$DownloadOnly
