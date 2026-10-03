# AGENTS.md

Guidance for AI coding agents. The first section is for agents that want to **use** pyvswitch on a
machine where it is installed; the second is for agents **working on this repository**.

## Using pyvswitch

pyvswitch is a Python version manager for Windows. When it is installed, the `pyvswitch` command is
on PATH. Add `--json` to any command for structured output.

| Goal | Command |
| --- | --- |
| List installed interpreters | `pyvswitch list --json` |
| Show the active interpreter | `pyvswitch current --json` |
| Make a version the default `python` | `pyvswitch use 3.12` |
| List versions available to download | `pyvswitch available` |
| Download and install a version | `pyvswitch install 3.13` (add `--use` to activate it) |
| Install packages into one version | `pyvswitch add requests "numpy<2" -p 3.12` |
| Remove or update packages | `pyvswitch remove requests -p 3.12`, `pyvswitch upgrade --all -p 3.12` |
| List packages | `pyvswitch packages -p 3.12 --json` |
| Run a specific version | `pyvswitch run -p 3.11 -- script.py` |
| Get an interpreter's path | `pyvswitch which -p 3.11` |
| Create a virtual environment | `pyvswitch venv .venv -p 3.12` |
| Diagnose PATH problems | `pyvswitch doctor --json` |

- `-p` accepts `3.12`, `3.12.4`, `3.10-32`, a `python.exe` path or a venv folder. Omitted means the
  active interpreter.
- `pyvswitch use` affects new shells only. In the current shell, call a version with
  `pyvswitch run -p <version> -- …`.
- `pyvswitch ai guide` prints the complete reference. `pyvswitch mcp` runs an MCP server with the
  same operations as tools. See [docs/AI.md](docs/AI.md) and [docs/CLI.md](docs/CLI.md).

## Working on this repository

### Layout

| Path | Contents |
| --- | --- |
| `src/PyVSwitch.Core` | The engine. Every operation lives here, behind `PyEngine`. Must stay trim and AOT compatible. |
| `src/PyVSwitch.Cli` | `pyvswitch.exe`: argument parsing, human and `--json` output, the MCP server. Published with native AOT. |
| `src/PyVSwitch.App` | `pyvswitchw.exe`: the WPF app. `Themes/` holds the design tokens and control styles, `Pages/` one XAML file per page, `ViewModels/` the logic. |
| `tests/PyVSwitch.Tests` | xUnit tests for the engine. |
| `installer/` | WiX v5 sources. |

### Commands

```powershell
dotnet build                                   # build everything
dotnet test                                    # run the tests
dotnet run --project src/PyVSwitch.Cli -- list # try the CLI
dotnet run --project src/PyVSwitch.App         # run the app
.\build-installer.ps1                          # tests + publish + MSI + setup EXE (use -NoAot without MSVC)
```

Run `build-installer.ps1` from PowerShell, not Git Bash: the native AOT link step needs
environment variables that Bash does not pass through.

### Conventions

- New behaviour goes in `PyVSwitch.Core` first, then is surfaced in the CLI, the MCP server and
  the app. The three surfaces must not grow their own copies of logic.
- Core code may not use reflection-based JSON. Add new serialized types to `CoreJsonContext`, or
  build output with `JsonObject` in `JsonShapes`.
- Anything that changes PATH must go through `PathSwitchService.WritePath` and record a
  `PathBackup`, so it can be rolled back.
- Pure logic (PATH editing, selector matching, catalog building, config file edits) is written as
  static functions with tests. Add tests alongside new logic of that kind.
- In XAML, colors come only from the palette brushes (`{DynamicResource …}`) so both themes work.
  There is deliberately no implicit `TextBlock` style.
- Errors meant for the user are thrown as `PyvsException(code, message)`; the code becomes the
  `error.code` in JSON output.

### Checking UI changes

The app can render every page to PNG files without touching the desktop:

```powershell
dotnet build src/PyVSwitch.App
.\src\PyVSwitch.App\bin\Debug\net9.0-windows\pyvswitchw.exe --demo --screenshots .\artifacts\shots
```

`--demo` uses built-in sample data; leave it out to render with the machine's real data. Add
`--light` for the light theme. The run also audits the interface wiring: binding errors and
buttons whose command did not resolve are written to
`%APPDATA%\PythonVersionSwitch\logs\app.log` with the prefix `BINDING`.

### Releasing

1. Update `Version` in `Directory.Build.props` and add a section to `CHANGELOG.md`.
2. Commit, then push a tag named `v<version>`.
3. The `release` workflow builds the installer and publishes a GitHub release with it.
