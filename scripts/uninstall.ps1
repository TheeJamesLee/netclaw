# Netclaw Windows uninstall script
#
# Usage:
#   iwr -useb https://releases.netclaw.dev/uninstall.ps1 | iex
#   .\uninstall.ps1 -DryRun
#   .\uninstall.ps1 -Purge
#   .\uninstall.ps1 -Purge -Yes
#   .\uninstall.ps1 -SkipShell
#   .\uninstall.ps1 -InstallDir C:\tools\netclaw
#
# -Purge deletes %USERPROFILE%\.netclaw (config, secrets, sessions, memory.db).
# It prompts for confirmation unless -Yes is also passed.
#
# Exit codes:
#   0  Netclaw is gone (including "was already gone")
#   1  The daemon would not stop; nothing was deleted
#   2  Invalid arguments
#   3  Refused: -Purge without -Yes when the session is not interactive
#   4  Refused: running from inside the install directory

param(
    [switch]$Purge,
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$SkipShell,
    [string]$InstallDir = "",
    [switch]$Help
)

$ErrorActionPreference = "Stop"

function Show-Usage {
    Write-Host @"
Netclaw Windows uninstall script

Usage:
  iwr -useb https://releases.netclaw.dev/uninstall.ps1 | iex
  .\uninstall.ps1 -DryRun
  .\uninstall.ps1 -Purge
  .\uninstall.ps1 -Purge -Yes
  .\uninstall.ps1 -SkipShell
  .\uninstall.ps1 -InstallDir C:\tools\netclaw

Arguments:
  -Purge              Also delete %USERPROFILE%\.netclaw (config, secrets,
                      sessions, memory.db). Prompts unless -Yes is passed.
  -Yes                Skip the confirmation prompt. Required for -Purge when
                      the session is not interactive.
  -DryRun             Report what would be removed; change nothing.
  -SkipShell          Do not touch the User PATH.
  -InstallDir <path>  Override the detected install directory.
  -Help               Show this help.

Exit codes:
  0  Netclaw is gone (including "was already gone")
  1  The daemon would not stop; nothing was deleted
  2  Invalid arguments
  3  Refused: -Purge without -Yes when the session is not interactive
  4  Refused: running from inside the install directory
"@
}

if ($Help) {
    Show-Usage
    exit 0
}

# This script edits HKCU\Environment and deletes .exe files. Running it on a
# non-Windows host would fail in confusing ways, so refuse up front.
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    Write-Error "This script uninstalls Netclaw on Windows. On Linux or macOS, use uninstall.sh instead."
    exit 2
}
if ($PSVersionTable.PSEdition -ne 'Core' -and -not ($env:OS -eq 'Windows_NT')) {
    Write-Error "This script uninstalls Netclaw on Windows. On Linux or macOS, use uninstall.sh instead."
    exit 2
}

# ── Netclaw home resolution ──
# Mirrors install.ps1:123-147 so the two scripts agree on the directory.
function Resolve-NetclawHome {
    $profileDir = $env:USERPROFILE
    $value = if ($env:NETCLAW_HOME) { $env:NETCLAW_HOME.Trim() } else { "" }
    if (-not $value) {
        return Join-Path $profileDir ".netclaw"
    }

    if ($value.StartsWith("~")) {
        $rest = $value.Substring(1).TrimStart('/', '\')
        $value = if ($rest) { Join-Path $profileDir $rest } else { $profileDir }
    }

    foreach ($token in @('$HOME', '${HOME}', '%USERPROFILE%')) {
        $value = [regex]::Replace($value, [regex]::Escape($token), { param($match) $profileDir }, 'IgnoreCase')
    }

    if (-not [System.IO.Path]::IsPathRooted($value)) {
        $value = Join-Path (Get-Location).ProviderPath $value
    }

    return [System.IO.Path]::GetFullPath($value)
}

function Remove-TrailingDirectorySeparators {
    param([string]$Path)

    if ([string]::IsNullOrEmpty($Path)) {
        return $Path
    }

    $root = [System.IO.Path]::GetPathRoot($Path)
    if ($Path -eq $root) {
        return $Path
    }

    $separators = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $trimmed = $Path.TrimEnd($separators)
    if ([string]::IsNullOrEmpty($trimmed) -and -not [string]::IsNullOrEmpty($root)) {
        return $root
    }

    return $trimmed
}

$NetclawHome = Resolve-NetclawHome

# ── Install directory resolution ──
# Mirrors install.ps1:118 and :149-151. Windows has no legacy env-script
# directory, so one path covers both cases.
$DefaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\netclaw"
if (-not $InstallDir) {
    $InstallDir = $DefaultInstallDir
}
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$InstallDirNormalized = Remove-TrailingDirectorySeparators $InstallDir

# ── Safety: refuse to run from inside the install directory ──
if (Test-Path -LiteralPath $InstallDir) {
    $installPhysical = (Get-Item -LiteralPath $InstallDir).FullName
    $cwd = (Get-Location).ProviderPath
    if ($cwd -eq $installPhysical -or $cwd.StartsWith($installPhysical + [System.IO.Path]::DirectorySeparatorChar)) {
        Write-Error "Refusing to run from inside the install directory ($installPhysical). Change to another directory and run again."
        exit 4
    }
}

# ── Safety: -Purge needs confirmation ──
if ($Purge -and -not $Yes -and -not $DryRun) {
    # [Environment]::UserInteractive is true for any console process, including
    # CI, so it cannot detect a redirected stdin. IsInputRedirected can: a
    # piped or redirected stdin means the script cannot prompt.
    $canPrompt = -not [Console]::IsInputRedirected
    if (-not $canPrompt) {
        Write-Error "-Purge deletes $NetclawHome and needs confirmation. stdin is redirected, so the script cannot prompt. Pass -Yes to confirm, or omit -Purge to keep your data."
        exit 3
    }
    Write-Host "This will permanently delete:"
    Write-Host "  $NetclawHome"
    Write-Host ""
    Write-Host "That directory holds your config, secrets, sessions, and memory database."
    $confirmation = Read-Host "Type 'yes' to continue"
    if ($confirmation -ne "yes") {
        Write-Host "Aborted. Nothing was deleted."
        exit 0
    }
}

# ── Reporting helpers ──
$Removed = New-Object System.Collections.Generic.List[string]
$Kept = New-Object System.Collections.Generic.List[string]
$Warnings = New-Object System.Collections.Generic.List[string]

function Remove-TargetFile {
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    if ($DryRun) {
        Write-Host "  would remove: $Path"
        $Removed.Add($Label)
        return
    }

    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Write-Host "  removed: $Path"
        $Removed.Add($Label)
    } catch {
        Write-Host "  warning: could not remove $Path : $_" -ForegroundColor Yellow
        $Warnings.Add("could not remove $Path")
    }
}

Write-Host "Netclaw uninstaller"
Write-Host "  Install dir: $InstallDir"
Write-Host "  Netclaw home: $NetclawHome"
if ($Purge) {
    Write-Host "  Mode: purge (data will be deleted)"
} else {
    Write-Host "  Mode: standard (data will be kept)"
}
if ($DryRun) {
    Write-Host "  Dry run: no changes will be made"
}
Write-Host ""

# ── 1. Stop the daemon ──
# Must happen before any deletion. A running netclawd.exe is a mapped image
# section; DeleteFile fails with UnauthorizedAccessException.
Write-Host "Stopping the daemon..."
$pidFile = Join-Path $NetclawHome "netclaw.pid"
$daemonPid = $null
if (Test-Path -LiteralPath $pidFile) {
    $pidText = (Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue)
    if ($pidText) {
        $parsed = 0
        if ([int]::TryParse($pidText.Trim(), [ref]$parsed)) {
            $daemonPid = $parsed
        }
    }
}

$daemonRunning = $false
if ($null -ne $daemonPid) {
    $proc = Get-Process -Id $daemonPid -ErrorAction SilentlyContinue
    if ($null -ne $proc) {
        $daemonRunning = $true
    }
}

if ($daemonRunning) {
    if ($DryRun) {
        Write-Host "  would stop daemon (pid $daemonPid)"
    } else {
        $cliPath = Join-Path $InstallDir "netclaw.exe"
        if (Test-Path -LiteralPath $cliPath) {
            & $cliPath daemon stop *>&1 | Out-Null
        } else {
            Stop-Process -Id $daemonPid -ErrorAction SilentlyContinue
        }

        $waited = 0
        while ($waited -lt 30 -and (Get-Process -Id $daemonPid -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 1
            $waited++
        }

        if (Get-Process -Id $daemonPid -ErrorAction SilentlyContinue) {
            Write-Error "The daemon (pid $daemonPid) did not stop within 30 seconds. Nothing was deleted. Stop it manually and run again."
            exit 1
        }
        Write-Host "  daemon stopped (pid $daemonPid)"
    }
} else {
    Write-Host "  no running daemon"
}

# ── 2. Remove the daemon environment file ──
Remove-TargetFile (Join-Path $NetclawHome "config\daemon.env") "daemon environment file"

# ── 3. Remove binaries and update leftovers ──
Write-Host ""
Write-Host "Removing binaries..."
Remove-TargetFile (Join-Path $InstallDir "netclaw.exe") "netclaw binary"
Remove-TargetFile (Join-Path $InstallDir "netclawd.exe") "netclawd binary"
Remove-TargetFile (Join-Path $InstallDir "netclaw.exe.backup") "netclaw backup"
Remove-TargetFile (Join-Path $InstallDir "netclawd.exe.backup") "netclawd backup"

# Remove the install directory when it is empty. A custom -InstallDir may hold
# other files, so only delete it when nothing remains.
if (-not $DryRun -and (Test-Path -LiteralPath $InstallDir)) {
    $remaining = @(Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue)
    if ($remaining.Count -eq 0) {
        try {
            Remove-Item -LiteralPath $InstallDir -Force -ErrorAction Stop
            Write-Host "  removed: $InstallDir"
            $Removed.Add("install directory")
        } catch {
            Write-Host "  warning: could not remove $InstallDir : $_" -ForegroundColor Yellow
            $Warnings.Add("could not remove $InstallDir")
        }
    } else {
        Write-Host "  kept: $InstallDir (contains other files)"
        $Kept.Add("install directory ($InstallDir)")
    }
}

# ── 4. Remove the User PATH entry ──
if (-not $SkipShell) {
    Write-Host ""
    Write-Host "Removing User PATH entry..."

    $userEnvironmentKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Environment", $true)
    if ($null -eq $userEnvironmentKey) {
        Write-Host "  warning: cannot open the current user's Environment registry key" -ForegroundColor Yellow
        $Warnings.Add("cannot open HKCU\Environment")
    } else {
        try {
            $pathValueExists = $userEnvironmentKey.GetValueNames() -contains "Path"
            if (-not $pathValueExists) {
                Write-Host "  no User PATH value to edit"
            } else {
                # Read the raw value so %VAR% references survive the rewrite.
                $userPath = $userEnvironmentKey.GetValue(
                    "Path",
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $userPathKind = $userEnvironmentKey.GetValueKind("Path")

                if ($null -ne $userPath -and $userPath -isnot [string]) {
                    throw "The current user's PATH registry value is not a string."
                }

                $entries = if ([string]::IsNullOrEmpty($userPath)) { @() } else {
                    $userPath -split ';' | Where-Object { -not [string]::IsNullOrEmpty($_) }
                }

                # Match on the expanded form so a %VAR% entry that resolves to
                # the install dir is removed too. Compare normalized paths so a
                # trailing separator does not defeat the match.
                $keptEntries = @()
                $removedCount = 0
                foreach ($entry in $entries) {
                    $expanded = [Environment]::ExpandEnvironmentVariables($entry)
                    $normalized = Remove-TrailingDirectorySeparators $expanded
                    if ($normalized -eq $InstallDirNormalized) {
                        $removedCount++
                    } else {
                        $keptEntries += $entry
                    }
                }

                if ($removedCount -eq 0) {
                    Write-Host "  install directory is not on the User PATH"
                } elseif ($DryRun) {
                    Write-Host "  would remove $removedCount User PATH entry/entries"
                    $Removed.Add("User PATH entry")
                } else {
                    $newUserPath = $keptEntries -join ';'
                    $userEnvironmentKey.SetValue("Path", $newUserPath, $userPathKind)
                    Write-Host "  removed $removedCount User PATH entry/entries"
                    $Removed.Add("User PATH entry")

                    # Tell Windows the environment changed so new terminals pick
                    # it up. Failure here is cosmetic, not fatal.
                    if (-not ("NetclawUninstaller.NativeMethods" -as [type])) {
                        Add-Type -Namespace NetclawUninstaller -Name NativeMethods -MemberDefinition @'
                            [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
                            public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg,
                                UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
                    }
                    $broadcastOutput = [UIntPtr]::Zero
                    $broadcastResult = [NetclawUninstaller.NativeMethods]::SendMessageTimeout(
                        [IntPtr]0xFFFF, 0x001A, [UIntPtr]::Zero, "Environment", 2, 1000, [ref]$broadcastOutput)
                    if ($broadcastResult -eq [IntPtr]::Zero) {
                        Write-Host "  warning: Windows did not acknowledge the environment-change notification. New terminals may need a sign-out." -ForegroundColor Yellow
                    }
                }
            }
        } finally {
            $userEnvironmentKey.Dispose()
        }
    }
} else {
    Write-Host ""
    Write-Host "Skipping User PATH (SkipShell)."
    $Kept.Add("User PATH entry")
}

# ── 5. Purge the data directory ──
if ($Purge) {
    Write-Host ""
    Write-Host "Removing Netclaw home..."
    if (Test-Path -LiteralPath $NetclawHome) {
        if ($DryRun) {
            Write-Host "  would remove: $NetclawHome"
            $Removed.Add("Netclaw home")
        } else {
            try {
                Remove-Item -LiteralPath $NetclawHome -Recurse -Force -ErrorAction Stop
                Write-Host "  removed: $NetclawHome"
                $Removed.Add("Netclaw home")
            } catch {
                Write-Host "  warning: could not fully remove $NetclawHome : $_" -ForegroundColor Yellow
                $Warnings.Add("could not fully remove $NetclawHome")
            }
        }
    }
} else {
    $Kept.Add("Netclaw home ($NetclawHome)")
}

# ── Summary ──
Write-Host ""
Write-Host "Summary"
if ($Removed.Count -eq 0) {
    Write-Host "  Nothing to remove. Netclaw was already uninstalled."
} else {
    Write-Host "  Removed:"
    foreach ($item in $Removed) { Write-Host "    - $item" }
}

if ($Kept.Count -gt 0) {
    Write-Host "  Kept:"
    foreach ($item in $Kept) { Write-Host "    - $item" }
}

if ($Warnings.Count -gt 0) {
    Write-Host "  Warnings:"
    foreach ($item in $Warnings) { Write-Host "    - $item" }
}

if (-not $Purge -and (Test-Path -LiteralPath $NetclawHome)) {
    Write-Host ""
    Write-Host "Your data is still at $NetclawHome."
    Write-Host "To remove it too, run this script again with -Purge."
}

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run complete. Nothing was changed."
}

exit 0
