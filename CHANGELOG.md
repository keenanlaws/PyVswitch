# Changelog

All notable changes to pyvswitch are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-03

A ground-up rework. pyvswitch grows from a tray PATH switcher into a full Python version manager
with a new interface, a command line and first-class support for AI assistants.

### Added

- **New desktop app.** A redesigned interface with a navigation sidebar, Overview, Versions,
  Get Python, Packages, Doctor, AI & CLI and Settings pages, dark and light themes (or follow
  Windows), in-app notifications and a live activity log for long-running work.
- **Install any Python.** Browse every release python.org ships a Windows installer for, with its
  support status and end-of-life date. pyvswitch downloads the official installer, verifies its
  size and checksum, and installs it unattended for the current user. 64-bit, 32-bit and ARM64.
- **Packages per interpreter.** Install, update and remove libraries for one specific Python or
  virtual environment. Includes PyPI lookup with version selection, update checks, "update all",
  and `requirements.txt` import and export.
- **Virtual environments.** Create a venv from any installed version and manage its packages.
- **Doctor.** Detects System PATH entries that override your selection, Microsoft Store alias
  stubs, dead and duplicate PATH entries, a missing pip, leaked `PYTHONHOME` / `PYTHONPATH`, and a
  `py` launcher that disagrees with `python`. Shows the PATH search order and can clean it up.
- **Command line.** `pyvswitch.exe`, a native executable with commands for everything the app
  does and `--json` output on every command. See [docs/CLI.md](docs/CLI.md).
- **MCP server.** `pyvswitch mcp` exposes 14 tools to Model Context Protocol clients.
- **AI integration.** `pyvswitch ai setup` (or the AI & CLI page) registers the CLI and MCP server
  with Claude Code, Claude Desktop, OpenAI Codex, Cursor, VS Code / GitHub Copilot, Gemini CLI and
  Windsurf. See [docs/AI.md](docs/AI.md).
- **Uninstall a Python version** from the app or the CLI.
- **Open a terminal** that uses a chosen version, without changing the default.
- Unit tests for the engine and GitHub Actions workflows for CI and releases.

### Changed

- The desktop app is now `pyvswitchw.exe` and the new CLI takes the `pyvswitch.exe` name,
  mirroring `python.exe` / `pythonw.exe`. The installer adds the install folder to PATH.
- PATH is read and written as raw registry data, so `%VARIABLE%` references in other entries are
  preserved instead of being flattened to literal paths.
- "Active Python" now means what a new terminal really resolves: System PATH entries are
  considered ahead of User PATH entries, and a mismatch is reported instead of hidden.
- Rollback restores the `py` launcher default along with PATH.
- Interpreter architecture is read from the executable itself instead of being guessed from the
  folder name.
- Discovery also scans the standard install folders and pyenv-win, and skips Microsoft Store alias
  stubs and virtual environments.
- The app, the CLI and the MCP server share one engine and one settings file, and the app updates
  live when another of them makes a change.
- Only one instance of the tray app runs; starting it again brings the existing window forward.

### Removed

- The built-in self-uninstall script. pyvswitch is removed from Windows Settings like any other
  app.
- Installing Python through winget. The python.org installer flow replaces it and covers every
  version.

### Upgrade notes

- Installing 1.0.0 over 0.4.0 replaces it. Settings and PATH history are kept.
- A "start with Windows" entry from 0.x is updated to launch the tray app.

## [0.4.0] - 2026-07-08

Initial public release: a tray utility that switches the Python version at the front of PATH, with
User and System scopes, backups and rollback, `py.ini` updates, a health check and an MSI
installer.

[1.0.0]: https://github.com/keenanlaws/PyVswitch/releases/tag/v1.0.0
[0.4.0]: https://github.com/keenanlaws/PyVswitch/releases/tag/v0.4.0
