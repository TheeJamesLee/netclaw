# NetClaw Install Write-Inventory

Read-only analysis of `feature/scripted-uninstall` @ `26e3358`.
All citations are `file:line`.

Purpose: give the uninstall implementations an exact inverse for every side
effect the installers produce.

---

## 1. Write inventory table

| # | Surface | Platform | Exact path or registry key | Written by | Condition | Exact inverse operation | Notes |
|---|---------|----------|---------------------------|-----------|-----------|------------------------|-------|
| 1 | CLI binary | Linux/macOS | `$INSTALL_DIR/netclaw` | `install.sh:350` (`cp`), `:351` (`chmod +x`) | Always when component is `all` or `cli` | `rm -f "$INSTALL_DIR/netclaw"` | `INSTALL_DIR` resolution is at `install.sh:215-225` |
| 2 | Daemon binary | Linux/macOS | `$INSTALL_DIR/netclawd` | `install.sh:350`, `:351` | Always when component is `all` or `daemon` | `rm -f "$INSTALL_DIR/netclawd"` | Same directory as the CLI |
| 3 | CLI binary | Windows | `$InstallDir\netclaw.exe` | `install.ps1:260` (`Copy-Item`) | Component `all` or `cli` | `Remove-Item "$InstallDir\netclaw.exe"` | Default `$InstallDir` = `%LOCALAPPDATA%\Programs\netclaw` (`install.ps1:118`) |
| 4 | Daemon binary | Windows | `$InstallDir\netclawd.exe` | `install.ps1:260` | Component `all` or `daemon` | `Remove-Item "$InstallDir\netclawd.exe"` | |
| 5 | POSIX env script | Linux/macOS | `$ENV_HOME_DIR/env` | `install.sh:476-495` (`write_posix_env_script`) | `--skip-shell` not passed | `rm -f "$ENV_HOME_DIR/env"` | `ENV_HOME_DIR` = `$NETCLAW_HOME` normally, `$HOME/.netclaw` for a legacy install (`install.sh:213`, `:221`) |
| 6 | bash rc block | Linux | `~/.bashrc` | `install.sh:511-514` | Shell is bash, `--skip-shell` not passed | Delete the exact 2-line block | See §2 for exact text |
| 7 | bash rc block | macOS | `~/.bash_profile`, else `~/.bash_login`, else `~/.profile` | `install.sh:511-514`, selection at `:459-467` | Shell is bash, `--skip-shell` not passed | Delete the exact 2-line block from the file that holds it | macOS login shell reads only the first existing file |
| 8 | zsh rc block | Linux/macOS | `$ZDOTDIR/.zshrc` (resolved via `zsh -c`) | `install.sh:511-514`, resolution at `:446-455` | Shell is zsh, `ZDOTDIR` resolvable | Delete the exact 2-line block | `ZDOTDIR` is often set without `export` in `.zshenv`; the installer asks zsh for the effective value |
| 9 | fish conf.d file | Linux/macOS | `${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/netclaw.fish` | `install.sh:519-531` (`write_fish_config`) | Shell is fish, `--skip-shell` not passed | `rm -f` that file | Whole file is netclaw-owned; safe to delete outright |
| 10 | Windows User PATH | Windows | `HKCU\Environment` value `Path` | `install.ps1:331` (open key), `:388` (`SetValue`) | `-SkipShell` not passed | Read with `DoNotExpandEnvironmentNames`, split on `;`, drop entries whose expanded form equals the install dir, rejoin, write back with the original `RegistryValueKind` | Must preserve `REG_EXPAND_SZ` and `%VAR%` text |
| 11 | WM_SETTINGCHANGE broadcast | Windows | n/a (message) | `install.ps1:420` | Only when the User PATH actually changed | Broadcast again after removal | Cosmetic; new terminals pick up the change |
| 12 | Config seed | Linux/macOS | `$NETCLAW_HOME/config/netclaw.json` | `install.sh:410` | Only when `--channel` passed AND config does not exist | Delete only if the file contains nothing but the seed | Destructive: the file may hold operator config. See §5 |
| 13 | Config seed | Windows | `$NETCLAW_HOME\config\netclaw.json` | `install.ps1:314-315` | Only when `-Channel` passed AND config does not exist | Same as row 12 | |
| 14 | systemd unit | Linux | `~/.config/systemd/user/netclaw.service` | `DaemonManager.cs:504-508`, path at `:555-559` | `netclaw daemon install` ran | `systemctl --user stop/disable`, delete file, `daemon-reload` | Already implemented in `UninstallAsync` (`DaemonManager.cs:614-637`) |
| 15 | daemon.env | Linux | `$NETCLAW_HOME/config/daemon.env` | `DaemonManager.cs:497-502`, path at `NetclawPaths.cs:104` | `netclaw daemon install` ran | Delete the file | `RemoveDaemonEnvironmentFile` (`DaemonManager.cs:568-573`) |
| 16 | `.backup` files | Linux/macOS | `$INSTALL_DIR/netclaw.backup`, `netclawd.backup` | `UpdateCommand.cs:339-340` | A self-update ran | `rm -f` both | On Windows the running image's backup cannot be deleted in-process |
| 17 | `.backup` files | Windows | `$InstallDir\netclaw.exe.backup`, `netclawd.exe.backup` | `UpdateCommand.cs:336-340` | A self-update ran | `Remove-Item` both | Uninstall runs outside the install dir, so both are deletable |
| 18 | Runtime data | Linux/macOS | `$NETCLAW_HOME` (default `~/.netclaw`) | `NetclawPaths.cs:140-149` | First CLI run | `rm -rf` only under `--purge` | Holds `secrets.json`, `netclaw.db`, sessions, identity |
| 19 | Runtime data | Windows | `%USERPROFILE%\.netclaw` | `NetclawPaths.cs:140-149` | First CLI run | `Remove-Item -Recurse` only under `-Purge` | |

---

## 2. Exact text written

### 2.1 POSIX env script (`install.sh:478-494`)

```sh
#!/bin/sh
# netclaw shell setup
netclaw_bin='<INSTALL_DIR>'
case ":${PATH:-}:" in
    *:"${netclaw_bin}":*)
        ;;
    *)
        if [ -n "${PATH:-}" ]; then
            export PATH="${netclaw_bin}:${PATH}"
        else
            export PATH="${netclaw_bin}"
        fi
        ;;
esac
unset netclaw_bin
```

`<INSTALL_DIR>` is shell-quoted by `shell_quote` (`install.sh:420-424`), which
wraps the value in single quotes and escapes embedded single quotes as `'\''`.

### 2.2 rc block (`install.sh:511-514`)

Exactly two lines, appended:

```
# netclaw shell setup
. '<ENV_SCRIPT>'
```

`SOURCE_LINE` is `. $ENV_SCRIPT_QUOTED` (`install.sh:428`), where
`ENV_SCRIPT_QUOTED` is the shell-quoted `$ENV_HOME_DIR/env`.

The installer guards against duplicates with `grep -qxF "$SOURCE_LINE"`
(`install.sh:502`), so the source line appears at most once. The comment line
is **not** guarded — a hand-edited profile could hold the comment twice.

### 2.3 fish conf.d (`install.sh:523-529`)

```fish
# netclaw shell setup
set -l netclaw_bin '<INSTALL_DIR>'
if not contains -- $netclaw_bin $PATH
    set -gx PATH $netclaw_bin $PATH
end
```

### 2.4 systemd unit (`DaemonManager.cs:592-609`)

```ini
[Unit]
Description=Netclaw Daemon
After=network.target

[Service]
Type=simple
ExecStart=<binaryPath>
ExecStop=<cliBinaryPath> daemon stop
TimeoutStopSec=<seconds>
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
EnvironmentFile=-<environmentFilePath>

[Install]
WantedBy=default.target
```

---

## 3. Ordering constraints

1. **Stop the daemon before deleting binaries.** On Windows a running
   `netclawd.exe` is a mapped image section; `DeleteFile` fails with
   `UnauthorizedAccessException`. On Linux the daemon holds the SQLite WAL
   (`netclaw.db-wal`) open, so deleting `$NETCLAW_HOME` under a live daemon
   leaves a corrupt database.
2. **Remove the systemd unit before deleting the daemon binary.** The unit's
   `ExecStop=` points at the CLI binary (`DaemonManager.cs:600`). If the binary
   is gone, `systemctl --user stop` cannot run the graceful stop path and
   systemd falls back to `SIGTERM` after `TimeoutStopSec`.
3. **Remove the rc block before deleting the env script.** A shell that sources
   a missing file prints an error on every new terminal. The reverse order
   leaves a broken profile between the two steps.
4. **Delete `.backup` files after the binaries.** On Windows the backup of the
   running image cannot be deleted while that image runs. Uninstall runs
   outside the install dir, so this is only a concern if the operator launched
   uninstall from inside it.
5. **`--purge` last.** Deleting `$NETCLAW_HOME` removes `config/daemon.env`,
   which the systemd unit references. Remove the unit first.

---

## 4. Idempotency hazards

| Operation | Target already absent | Safe to repeat |
|-----------|----------------------|----------------|
| `rm -f` a binary | No-op, exit 0 | Yes |
| `rm -f` the env script | No-op | Yes |
| Delete the rc block | `grep` finds nothing; no edit | Yes |
| `rm -f` the fish conf.d file | No-op | Yes |
| Drop a PATH entry | Entry not found; PATH unchanged | Yes |
| `systemctl --user stop` on a stopped unit | Exit 0 | Yes |
| `systemctl --user disable` on a disabled unit | Exit 0 | Yes |
| `rm -f` the unit file | No-op | Yes |
| `rm -f` `daemon.env` | No-op | Yes |
| `rm -rf $NETCLAW_HOME` | No-op | Yes |

Hazards that are **not** idempotent by default:

- **rc-file rewrite.** A naive `sed -i` range delete can consume unrelated
  lines if the block appears twice or the file lacks a trailing newline. Use
  exact-line matching and verify the result.
- **PATH rewrite.** Rejoining a split PATH can drop an empty entry or change
  the registry value kind. Preserve both.
- **`systemctl --user daemon-reload`** on a host without a user systemd
  instance fails. Treat as a warning, not a fatal error.

---

## 5. Gaps and surprises

These are discrepancies against `docs/research/uninstall-plan.md`.

1. **The config seed is not safely deletable.** The plan lists
   `config/netclaw.json` as installer-written state. It is only written when
   the file does not already exist (`install.sh:407-412`). Once the operator
   runs `netclaw init`, that file holds real configuration. Uninstall must
   **not** delete it outside `--purge`. The plan's scenario table does not say
   this.

2. **`ENV_HOME_DIR` can differ from `NETCLAW_HOME`.** For a legacy install
   (`install.sh:219-222`), binaries live in `$HOME/.netclaw/bin` and the env
   script in `$HOME/.netclaw/env`, even when `NETCLAW_HOME` points elsewhere.
   Uninstall must resolve both paths with the same rule order as
   `install.sh:215-225`, not assume one directory.

3. **The rc comment line is not duplicate-guarded.** `install.sh:502` guards
   only the source line. A profile can hold `# netclaw shell setup` more than
   once. Uninstall must remove every occurrence of the comment that directly
   precedes a netclaw source line, and must not remove a stray comment that
   does not.

4. **macOS has no service registration in code.** `SPEC-011` describes a
   LaunchAgent at `~/Library/LaunchAgents/com.stannardlabs.netclaw.plist`, but
   no code writes it. `DaemonManager.InstallAsync` returns "not yet supported"
   on macOS (`DaemonManager.cs:456-463`). Uninstall must not attempt LaunchAgent
   removal; there is nothing to remove.

5. **Windows has no service registration at all.** `InstallAsync` rejects
   Windows (`DaemonManager.cs:462-463`). Uninstall only stops a running daemon
   process.

6. **The updater's `.backup` files are not in the plan's scenario table.** They
   are listed in the "What the installers write" table but no scenario row
   covers them. Add rows for both platforms.

7. **`install.ps1` does not write an env script.** Windows PATH wiring is
   registry-only. There is no file to delete on Windows, and no rc block.

8. **`NETCLAW_HOME` resolution differs between the two installers.** Both
   expand `~`, `$HOME`, `${HOME}`, and `%USERPROFILE%` case-insensitively, but
   `install.ps1` resolves a relative path against the PowerShell location
   (`install.ps1:143`) while `install.sh` resolves against `$PWD`
   (`install.sh:193`). Uninstall must match its own platform's rule.
