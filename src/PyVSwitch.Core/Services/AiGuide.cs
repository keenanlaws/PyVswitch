namespace PyVSwitch.Services;

/// <summary>The text pyvswitch gives to AI assistants so they know it exists and how to drive it.</summary>
public static class AiGuide
{
    public const string BeginMarker = "<!-- pyvswitch:begin -->";
    public const string EndMarker = "<!-- pyvswitch:end -->";

    /// <summary>Short block written into always-loaded instruction files, so it stays cheap in context.</summary>
    public static string InstructionBlock => $"""
        {BeginMarker}
        ## pyvswitch (Python version manager installed on this Windows machine)

        Use the `pyvswitch` CLI for anything involving Python versions or per-version packages. Add `--json` for structured output.

        - `pyvswitch list` / `pyvswitch current` - installed interpreters / the one active on PATH
        - `pyvswitch use 3.12` - make a version the default `python` (applies to new shells, not the current one)
        - `pyvswitch available` / `pyvswitch install 3.13` - browse and install any python.org release
        - `pyvswitch add <pkg>... -p 3.12` / `remove` / `upgrade` / `packages -p 3.12` - libraries for one specific interpreter
        - `pyvswitch run -p 3.11 -- script.py` / `pyvswitch which -p 3.11` - run or locate a version without switching
        - `pyvswitch venv .venv -p 3.12` / `pyvswitch doctor` - create a virtual environment / diagnose PATH problems

        Run `pyvswitch ai guide` for the full reference. An MCP server is available via `pyvswitch mcp`.
        {EndMarker}
        """;

    public static string FullGuide => $$$"""
        # pyvswitch {{{AppInfo.Version}}} - guide for AI assistants

        pyvswitch is a Python version manager for Windows. It is installed on this machine and the
        `pyvswitch` command is on PATH. It finds every installed Python, switches which one `python`
        resolves to, downloads and installs any release from python.org, and manages packages
        separately for each interpreter.

        ## Rules of thumb

        - Add `--json` to any command for machine-readable output:
          `{"ok":true,"data":...}` on success, `{"ok":false,"error":{"code":"...","message":"..."}}` on failure.
        - Exit code 0 means success; anything else is a failure (2 = bad usage, 3 = not found).
        - `-p` / `--python` selects an interpreter. It accepts `3.12`, `3.12.4`, `3.10-32`, `3.10-64`,
          a path to `python.exe`, or a virtual-environment folder. Omitted means the active interpreter.
        - `pyvswitch use` edits the persistent User PATH. Already-open shells keep their old PATH, so
          right after switching, call an interpreter explicitly with `pyvswitch run -p <version> -- ...`
          or use the path from `pyvswitch which -p <version>`.
        - Everything works without administrator rights unless `--scope system` or `--all-users` is used.

        ## Commands

        | Goal | Command |
        | --- | --- |
        | List installed interpreters | `pyvswitch list --json` |
        | Show the active interpreter | `pyvswitch current --json` |
        | Switch the active interpreter | `pyvswitch use 3.12` |
        | Undo the last switch | `pyvswitch rollback` |
        | List downloadable versions | `pyvswitch available` (`--series 3.12`, `--all`, `--pre`) |
        | Download and install a version | `pyvswitch install 3.13` (latest 3.13.x) or `pyvswitch install 3.13.5 --use` |
        | Install a 32-bit build | `pyvswitch install 3.12 --arch x86` |
        | Uninstall a version | `pyvswitch uninstall 3.9` |
        | Path of an interpreter | `pyvswitch which -p 3.12` |
        | Run Python with a specific version | `pyvswitch run -p 3.11 -- script.py --flag` |
        | List packages | `pyvswitch packages -p 3.12 --json` (`--outdated`) |
        | Install packages | `pyvswitch add requests "numpy<2" -p 3.12` |
        | Install from requirements | `pyvswitch add -r requirements.txt -p 3.12` |
        | Remove packages | `pyvswitch remove requests -p 3.12` |
        | Upgrade packages | `pyvswitch upgrade requests -p 3.12` or `pyvswitch upgrade --all -p 3.12` |
        | Export requirements | `pyvswitch freeze -p 3.12 -o requirements.txt` |
        | Look a package up on PyPI | `pyvswitch info requests --json` |
        | Raw pip for one interpreter | `pyvswitch pip -p 3.12 -- install -e .` |
        | Create a virtual environment | `pyvswitch venv .venv -p 3.12` |
        | Diagnose PATH problems | `pyvswitch doctor --json` (`--fix` repairs dead and duplicate entries) |
        | Open the desktop app | `pyvswitch gui` |

        ## Typical workflows

        Need a library in a specific Python:
            pyvswitch add pyserial -p 3.10-32

        Need a Python version that is not installed yet:
            pyvswitch install 3.13 --use
            pyvswitch run -p 3.13 -- -c "import sys; print(sys.version)"

        Project needs its own environment:
            pyvswitch venv .venv -p 3.12
            pyvswitch add -r requirements.txt -p .venv

        ## MCP server

        `pyvswitch mcp` runs a Model Context Protocol server over stdio that exposes the same
        operations as tools (list_pythons, switch_python, install_python, install_packages, ...).
        Register it with: `pyvswitch ai setup`.

        Source and docs: {{{AppInfo.RepoUrl}}}
        """;
}
