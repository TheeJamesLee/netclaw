# Plan: Scripted Uninstall for NetClaw

Status: proposal. No code written yet.
Date: 2026-10-25.

## Problem

NetClaw has no uninstall path. The install scripts write state in five places.
Nothing reverses those writes. An operator who wants NetClaw gone must read the
installer source and delete files by hand.

Current state:

- `scripts/install.sh` and `scripts/install.ps1` install only. Neither has an
  uninstall mode.
- `netclaw daemon uninstall` exists. It removes the Linux systemd user service
  only. It refuses to run on macOS and Windows.
- No `uninstall.sh`, no `uninstall.ps1`, no `netclaw uninstall` command.

## What the installers write

| Surface | Linux / macOS | Windows |
|---------|---------------|---------|
| Binaries | `$NETCLAW_HOME/bin/netclaw`, `netclawd` (or `INSTALL_DIR`) | `%LOCALAPPDATA%\Programs\netclaw\netclaw.exe`, `netclawd.exe` (or `-InstallDir`) |
| PATH wiring | `$NETCLAW_HOME/env` POSIX script | `HKCU\Environment` `Path` value, prepended |
| Shell profile | two-line block in `~/.bashrc`, `~/.zshrc`, or macOS login profile | none (registry only) |
| Fish | `~/.config/fish/conf.d/netclaw.fish` | n/a |
| Config seed | `$NETCLAW_HOME/config/netclaw.json` when `--channel` passed | same, when `-Channel` passed |
| Service | `~/.config/systemd/user/netclaw.service` + `config/daemon.env` | none (unsupported) |
| Runtime data | everything under `$NETCLAW_HOME` | everything under `%USERPROFILE%\.netclaw` |
| Update leftovers | `netclaw.backup`, `netclawd.backup` in the install dir | same |

`NetclawPaths` owns the runtime data layout. Uninstall must not hardcode a
second copy of that layout. It must ask `NetclawPaths` for the base path.

## Design decisions

### D1. Two entry points, one shared contract

Add `scripts/uninstall.sh` and `scripts/uninstall.ps1`. Each mirrors the
installer it reverses. Do not add a `netclaw uninstall` CLI command in the
first cut.

Reason: the CLI cannot delete its own running binary on Windows, and it cannot
remove the PATH entry that launched it without leaving the shell broken. A
script that runs outside the install directory has no such problem. The
installer is a script, so the uninstaller is a script.

A `netclaw uninstall` command can come later as a thin wrapper that shells out
to the script. That is a separate change.

### D2. Default is data-preserving

`uninstall.sh` with no flags removes binaries, PATH wiring, shell profile
lines, and the service unit. It leaves `$NETCLAW_HOME` intact.

`--purge` also deletes `$NETCLAW_HOME`. This is the destructive flag, so it
requires an explicit confirmation prompt unless `--yes` is also passed.

Reason: `$NETCLAW_HOME` holds `secrets.json`, `netclaw.db`, sessions, and
identity files. Silent deletion of that is unacceptable.

### D3. Idempotent and non-fatal

Every step checks before it acts. A missing file is success, not an error. The
script exits 0 when the end state is "NetClaw is gone", regardless of how much
was already gone.

Reason: operators run uninstall twice. Half-installed machines exist. A
teardown script that fails on a missing file is useless.

### D4. Stop the daemon first, and prove it stopped

Order matters. Stop the daemon, wait for the PID file to clear, then delete
binaries. On Windows a running `netclawd.exe` cannot be deleted. On Linux a
running daemon can hold the SQLite WAL open.

If the daemon does not stop within the budget, the script fails loudly and
tells the operator the PID. It does not delete files under a live process.

### D5. PATH removal is exact-match, not substring

Linux: delete the two-line block from the rc file by exact line match. Do not
use a loose `sed` range that could eat unrelated lines. Verify the block is
gone and that no other `netclaw` line remains.

Windows: read `HKCU\Environment` `Path` with
`DoNotExpandEnvironmentNames`, split on `;`, drop entries whose expanded form
equals the install dir, rejoin, and write back with the original
`RegistryValueKind`. This is the exact inverse of what `install.ps1` does. It
must preserve `REG_EXPAND_SZ` and `%VAR%` text.

### D6. Report what was removed and what was kept

Print a summary. Name every path touched. When `--purge` is absent, print the
data directory and say it was kept.

## Covered scenarios

| # | Platform | Install shape | Uninstall action | Data | Service |
|---|----------|---------------|------------------|------|---------|
| 1 | Linux | `install.sh` default (`$NETCLAW_HOME/bin`) | remove binaries, `env` script, rc block | kept | stop + remove unit if present |
| 2 | Linux | `install.sh` with `INSTALL_DIR` | remove binaries from that dir, `env` script, rc block | kept | stop + remove unit if present |
| 3 | Linux | `install.sh --skip-shell` | remove binaries only; no rc edit attempted | kept | stop + remove unit if present |
| 4 | Linux | bash rc | delete exact two-line block from `~/.bashrc` | kept | n/a |
| 5 | Linux | zsh rc, `ZDOTDIR` set | delete block from the resolved `.zshrc` | kept | n/a |
| 6 | Linux | fish | delete `~/.config/fish/conf.d/netclaw.fish` | kept | n/a |
| 7 | Linux | `netclaw daemon install` ran | `systemctl --user stop/disable`, delete unit, delete `daemon.env`, `daemon-reload` | kept | removed |
| 8 | Linux | daemon running, no unit | `netclaw daemon stop`, wait for PID clear | kept | stopped |
| 9 | Linux | `--purge` | all of the above plus `rm -rf $NETCLAW_HOME` | deleted | removed |
| 10 | Linux | update left `.backup` files | delete `netclaw.backup`, `netclawd.backup` | kept | n/a |
| 11 | Linux | already uninstalled | exit 0, report nothing to do | kept | n/a |
| 12 | macOS | `install.sh` default | remove binaries, `env` script, login-profile block | kept | stop only (no service support) |
| 13 | macOS | `--purge` | all of the above plus `rm -rf $NETCLAW_HOME` | deleted | stopped |
| 14 | Windows | `install.ps1` default | remove `%LOCALAPPDATA%\Programs\netclaw`, drop PATH entry | kept | stop only |
| 15 | Windows | `install.ps1 -InstallDir` | remove that dir, drop matching PATH entry | kept | stop only |
| 16 | Windows | `install.ps1 -SkipShell` | remove binaries; PATH untouched | kept | stop only |
| 17 | Windows | `REG_EXPAND_SZ` PATH | drop entry, keep `%VAR%` text and value kind | kept | n/a |
| 18 | Windows | `-Purge` | all of the above plus `Remove-Item %USERPROFILE%\.netclaw` | deleted | stopped |
| 19 | Windows | daemon running | `netclaw daemon stop`, wait, then delete | kept | stopped |
| 20 | Windows | already uninstalled | exit 0, report nothing to do | kept | n/a |
| 21 | Any | `NETCLAW_HOME` set to a custom path | resolve with `NetclawPaths` rules, act on that path | kept/deleted per flag | per platform |
| 22 | Any | `--dry-run` | print the plan, change nothing | untouched | untouched |

Out of scope for the first cut:

- macOS LaunchAgent removal. `DaemonManager` has no LaunchAgent code today.
  `SPEC-011` describes one, but the implementation does not exist. Add the
  removal path when the install path lands.
- Windows service removal. No Windows service install exists.
- Docker image teardown. Container removal is the operator's job.

## Command surface

### Linux / macOS

```bash
# Standard uninstall — removes binaries, PATH wiring, shell profile lines,
# and the systemd user service. Keeps ~/.netclaw (config, secrets, sessions).
curl -sSL https://releases.netclaw.dev/uninstall.sh | bash

# Or from a local checkout
bash scripts/uninstall.sh

# Preview what would be removed, change nothing
bash scripts/uninstall.sh --dry-run

# Remove everything, including ~/.netclaw (secrets, sessions, memory.db)
bash scripts/uninstall.sh --purge

# Non-interactive purge (CI, provisioning scripts)
bash scripts/uninstall.sh --purge --yes

# Leave shell profiles alone (you added PATH yourself)
bash scripts/uninstall.sh --skip-shell

# Custom install location
bash scripts/uninstall.sh --install-dir /opt/netclaw

# Custom home
NETCLAW_HOME=/srv/netclaw bash scripts/uninstall.sh --purge
```

### Windows

```powershell
# Standard uninstall — removes binaries and the User PATH entry.
# Keeps %USERPROFILE%\.netclaw.
iwr -useb https://releases.netclaw.dev/uninstall.ps1 | iex

# Or from a local checkout
.\scripts\uninstall.ps1

# Preview what would be removed, change nothing
.\scripts\uninstall.ps1 -DryRun

# Remove everything, including %USERPROFILE%\.netclaw
.\scripts\uninstall.ps1 -Purge

# Non-interactive purge
.\scripts\uninstall.ps1 -Purge -Yes

# Leave the User PATH alone
.\scripts\uninstall.ps1 -SkipShell

# Custom install location
.\scripts\uninstall.ps1 -InstallDir C:\tools\netclaw
```

### Flag reference

| Flag (Unix) | Flag (Windows) | Effect |
|-------------|----------------|--------|
| `--purge` | `-Purge` | Also delete `$NETCLAW_HOME` (secrets, sessions, memory.db) |
| `--yes` | `-Yes` | Skip the confirmation prompt. Required for `--purge` in a non-interactive shell |
| `--dry-run` | `-DryRun` | Print the removal plan, change nothing |
| `--skip-shell` | `-SkipShell` | Do not touch shell profiles or PATH |
| `--install-dir <path>` | `-InstallDir <path>` | Override the detected install directory |
| `--help` | `-Help` | Usage |

### What each mode removes

| Mode | Binaries | PATH / rc | Service | `$NETCLAW_HOME` |
|------|----------|-----------|---------|-----------------|
| default | yes | yes | yes | **kept** |
| `--skip-shell` | yes | no | yes | **kept** |
| `--purge` | yes | yes | yes | **deleted** |
| `--dry-run` | no | no | no | no |

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | NetClaw is gone (including "was already gone") |
| `1` | Daemon would not stop; nothing was deleted |
| `2` | Invalid arguments |
| `3` | Refused: `--purge` without `--yes` in a non-interactive shell |
| `4` | Refused: running from inside the install directory |

### Behavior notes

- The script is idempotent. Running it twice exits `0` both times.
- `--purge` prompts before deleting `$NETCLAW_HOME`. Answer `no` and the
  script still removes binaries, PATH wiring, and the service.
- On Windows, a running `netclawd.exe` cannot be deleted. The script stops the
  daemon first and fails loudly if it will not exit.
- macOS has no service registration today, so uninstall only stops a running
  daemon. There is no LaunchAgent to remove.
- Exit code `1` aborts before any deletion. A partial uninstall is worse than
  none: it leaves a machine with no CLI but a live daemon holding the SQLite
  WAL.
- `--purge` without `--yes` in a pipe is a hard refusal, not a prompt.
  `curl | bash` has no TTY, so a prompt would hang or read EOF as "yes".

## Implementation steps

1. `scripts/uninstall.sh`
   - argument parsing: `--purge`, `--yes`, `--dry-run`, `--skip-shell`,
     `--install-dir`, `--help`
   - resolve `NETCLAW_HOME` with the same rules as `install.sh`
   - stop the daemon, wait for the PID file to clear
   - remove the systemd unit and `daemon.env` when present
   - remove binaries and `.backup` files
   - remove the `env` script
   - remove the rc block by exact match; handle bash, zsh (`ZDOTDIR`), fish
   - `--purge`: delete `$NETCLAW_HOME`
   - print a summary
2. `scripts/uninstall.ps1`
   - same flags in PowerShell form: `-Purge`, `-Yes`, `-DryRun`, `-SkipShell`,
     `-InstallDir`
   - stop the daemon, wait
   - remove binaries and `.backup` files
   - remove the PATH entry, preserving value kind
   - broadcast `WM_SETTINGCHANGE` when PATH changed
   - `-Purge`: delete `%USERPROFILE%\.netclaw`
   - print a summary
3. Publish both scripts to the release feed. Add two `wrangler r2 object put`
   steps to `publish_release_binaries.yml` next to the install script uploads.
4. Document the commands in `README.md` and `docs/runbooks/`.
5. Add a `netclaw doctor` check that reports install state. Optional, later.

## Risks

| Risk | Mitigation |
|------|------------|
| `--purge` deletes secrets and sessions | confirmation prompt; `--yes` required for non-interactive use |
| rc-file edit corrupts a shell profile | exact-line match only; back up the rc file before editing; verify after |
| Windows PATH rewrite changes registry type | read and write with the original `RegistryValueKind`; test `REG_EXPAND_SZ` |
| running daemon holds files open | stop first, wait for PID clear, fail loudly on timeout |
| uninstall runs from the install dir | refuse to run when the current directory is the install dir |
| `NETCLAW_HOME` points at a shared or system path | refuse to purge a path that is not under the user profile unless `--force` |

## CI plan

See the companion section in the chat response. Summary: extend the existing
`install-smoke` job in `pr_validation.yml` with an uninstall phase, add a
round-trip assertion, and add a release-feed publish check.
