# pyvswitch command line

`pyvswitch.exe` is installed next to the desktop app and added to PATH. It is a native executable,
so it starts instantly and has no runtime to install.

```text
pyvswitch <command> [arguments] [--json]
```

## Selectors

Wherever a command takes a version, any of these work:

| Selector | Meaning |
| --- | --- |
| `3.12` | The newest installed 3.12.x. 64-bit is preferred when both builds exist. |
| `3.12.4` | That exact version. |
| `3.10-32`, `3.10-64`, `3.12-arm64` | A series with a specific build. |
| `3` | The newest installed Python 3. |
| `C:\Python312\python.exe` | A specific interpreter. |
| `.\.venv` | A virtual environment (or any folder containing `python.exe`). |

Commands that accept `-p` / `--python` use the **active interpreter** when it is left out.

## Output and exit codes

Add `--json` to any command for machine-readable output:

```json
{ "ok": true, "data": { "…": "…" } }
{ "ok": false, "error": { "code": "not_found", "message": "Python 3.11 is not installed …" } }
```

| Exit code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | The operation failed. |
| `2` | Bad usage. |
| `3` | Not found (version, package or active interpreter). |
| `130` | Cancelled with Ctrl+C. |

`run` and `pip` return the exit code of the Python process they started. Colors are disabled when
output is redirected or `NO_COLOR` is set.

## Versions

### `pyvswitch list`

Lists every interpreter found in the registry, through the `py` launcher, on PATH and in the usual
install folders. `--venvs` also lists virtual environments created with pyvswitch.

### `pyvswitch current`

Shows what `python` resolves to in a new terminal. Exit code 3 when nothing is on PATH.

### `pyvswitch use <version>`

Moves the version to the front of PATH and removes other interpreters' folders from it.

| Option | Effect |
| --- | --- |
| `--scope user` | Edit the User PATH (default, no admin needed). |
| `--scope system` | Edit the System PATH. Needs an elevated terminal. |
| `--no-launcher` | Leave the `py` launcher default (`py.ini`) alone. |

A shell that is already open keeps its old PATH. Use `run` to call the new version right away.

### `pyvswitch rollback`

Restores the PATH and `py` launcher default from before the most recent change. `--list` shows the
saved history (the 25 most recent changes are kept).

### `pyvswitch which [-p <version>]`

Prints the full path of an interpreter, for use in scripts:

```powershell
& (pyvswitch which -p 3.12) -m http.server
```

## Downloading Python

### `pyvswitch available`

One row per release series, with the newest installable version, its support status and what you
already have.

| Option | Effect |
| --- | --- |
| `--series 3.12` | Every release in one series, including source-only ones. |
| `--all` | Every release of every series. |
| `--pre` | Include alpha, beta and release-candidate series. |
| `--refresh` | Fetch the list again instead of using the 12 hour cache. |

### `pyvswitch install <version>`

Downloads the official installer from python.org, checks its size and checksum, and installs it
unattended for the current user.

| Version | Installs |
| --- | --- |
| `3.13` | The newest 3.13.x that has a Windows installer. |
| `3.13.5` | Exactly that release. |
| `latest` or `3` | The newest stable Python 3. |

| Option | Effect |
| --- | --- |
| `--arch x64\|x86\|arm64` | Build to install. Defaults to the machine's architecture. |
| `--use` | Make it the default `python` afterwards. |
| `--pre` | Allow pre-releases when resolving a series. |
| `--all-users` | Install for all users (asks for elevation). |
| `--target-dir <folder>` | Install somewhere other than the default location. |

Installing never changes PATH unless `--use` is given. Asking for a version that is already
installed succeeds and reports `alreadyInstalled`.

Two builds of the same series (for example 3.12.4 and 3.12.9, both 64-bit) cannot coexist: this is
how the python.org installer works. Installing a newer patch updates the existing one in place.

### `pyvswitch uninstall <version> --yes`

Runs the uninstaller that python.org's installer registered, then removes the version's folders
from the User PATH. Without `--yes` it asks for confirmation, or fails when there is no terminal to
ask in.

## Packages

Every command here runs pip inside the chosen interpreter only.

| Command | Effect |
| --- | --- |
| `pyvswitch packages [-p <version>] [--outdated]` | List packages. |
| `pyvswitch add <pkg>... [-p <version>] [--upgrade]` | Install packages. Version specifiers such as `"numpy<2"` are allowed. |
| `pyvswitch add -r requirements.txt [-p <version>]` | Install from a requirements file. |
| `pyvswitch remove <pkg>... [-p <version>]` | Uninstall packages. |
| `pyvswitch upgrade <pkg>... [-p <version>]` | Update named packages. |
| `pyvswitch upgrade --all [-p <version>]` | Update everything that is outdated. |
| `pyvswitch freeze [-p <version>] [-o requirements.txt]` | Export exact versions. |
| `pyvswitch info <pkg>` | Latest version, summary and recent versions from PyPI. |
| `pyvswitch pip [-p <version>] -- <pip arguments>` | Anything else pip can do. |

`add`, `remove` and `upgrade` refuse arguments that start with `-`, so a package name can never be
mistaken for a pip option. Use `pyvswitch pip -- …` when you need options:

```powershell
pyvswitch pip -p 3.12 -- install -e . --no-deps
```

## Running Python

### `pyvswitch run [-p <version>] -- <arguments>`

Runs that interpreter with the given arguments, attached to your terminal. The interpreter's own
folder and `Scripts` folder are put first on PATH for the child process, so anything it launches
sees the same version.

```powershell
pyvswitch run -p 3.10-32 -- flash.py --port COM3
pyvswitch run -p 3.13 -- -m pytest
```

### `pyvswitch venv <folder> [-p <version>]`

Creates a virtual environment with the built-in `venv` module. `--list` shows environments created
this way. Manage its packages with `-p <folder>`:

```powershell
pyvswitch venv .venv -p 3.12
pyvswitch add -r requirements.txt -p .venv
```

## Tools

### `pyvswitch doctor`

Checks what `python` resolves to, whether a System PATH entry or a Microsoft Store alias overrides
your choice, whether PATH has dead or duplicate Python entries, whether pip is present, and whether
the `py` launcher agrees. Exit code 1 when something needs attention.

`--fix` removes dead Python folders and duplicate entries from the User PATH (and saves a backup).

### `pyvswitch ai <status|setup|remove|guide>`

See [AI.md](AI.md).

### `pyvswitch mcp`

Runs a Model Context Protocol server on stdin/stdout. See [AI.md](AI.md).

### `pyvswitch gui`

Opens the desktop app.
