# pyvswitch for AI assistants

pyvswitch is designed to be driven by an AI assistant as easily as by a person. There are two ways
in, and they call the same engine:

- **The CLI.** Any assistant that can run a shell command can use `pyvswitch … --json`.
- **The MCP server.** `pyvswitch mcp` exposes the same operations as typed tools.

## One-step setup

```powershell
pyvswitch ai setup
```

For each supported tool that is installed on the PC, this does two things:

1. Adds a short block to the tool's global instruction file, so the assistant knows pyvswitch is
   installed and which commands to reach for.
2. Registers `pyvswitch mcp` as an MCP server.

| Command | Effect |
| --- | --- |
| `pyvswitch ai status` | Show which tools were found and what is registered. |
| `pyvswitch ai setup` | Register with every detected tool. |
| `pyvswitch ai setup --only claude-code,codex` | Register with specific tools. |
| `pyvswitch ai remove` | Take out everything `setup` added. |
| `pyvswitch ai guide` | Print the full guide for an assistant. `--short` prints the instruction block. |

Tool ids: `claude-code`, `claude-desktop`, `codex`, `cursor`, `gemini`, `vscode`, `windsurf`.

### What gets changed

| Tool | Instruction file | MCP configuration |
| --- | --- | --- |
| Claude Code | `%USERPROFILE%\.claude\CLAUDE.md` | via `claude mcp add --scope user pyvswitch` |
| Claude Desktop | n/a | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenAI Codex | `%USERPROFILE%\.codex\AGENTS.md` | `%USERPROFILE%\.codex\config.toml` |
| Cursor | n/a | `%USERPROFILE%\.cursor\mcp.json` |
| Gemini CLI | `%USERPROFILE%\.gemini\GEMINI.md` | `%USERPROFILE%\.gemini\settings.json` |
| VS Code / GitHub Copilot | `%APPDATA%\Code\User\prompts\pyvswitch.instructions.md` | `%APPDATA%\Code\User\mcp.json` |
| Windsurf | `%USERPROFILE%\.codeium\windsurf\memories\global_rules.md` | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` |

The rules pyvswitch follows when it edits these files:

- A tool that is not installed is skipped; nothing is created for it.
- The original file is copied to `<name>.pyvswitch.bak` before it is changed.
- Instruction files only ever gain one block, between `<!-- pyvswitch:begin -->` and
  `<!-- pyvswitch:end -->`. Everything else in the file is left byte-for-byte alone.
- JSON configuration only gains one `pyvswitch` entry under the MCP servers key. Other servers and
  settings are preserved. Comments in a JSON-with-comments file are not preserved when it is
  rewritten, which is why the backup exists.
- Claude Code's state file is never edited directly; registration goes through the `claude` CLI.
- Running `setup` twice changes nothing the second time. `remove` takes out exactly what was added.

Restart the assistant after setup so it reloads its configuration.

## Anything else

For a tool that is not listed:

- **Instructions:** run `pyvswitch ai guide` and paste the output into the tool's system prompt or
  rules file. The same text is saved to `%APPDATA%\PythonVersionSwitch\AI_GUIDE.md`.
- **MCP:** add a stdio server whose command is the full path to `pyvswitch.exe` with the single
  argument `mcp`:

```json
{
  "mcpServers": {
    "pyvswitch": {
      "command": "C:\\Program Files\\ecuunlock.com\\pyvswitch\\pyvswitch.exe",
      "args": ["mcp"]
    }
  }
}
```

## MCP server

`pyvswitch mcp` speaks JSON-RPC 2.0 over stdio, one message per line, and implements `initialize`,
`ping`, `tools/list` and `tools/call`. Tool results are JSON text in the same shapes the CLI prints
with `--json`. A failed tool call returns `isError: true` with a `code: message` string.

| Tool | Arguments | Notes |
| --- | --- | --- |
| `list_pythons` | none | Installed interpreters and the active path. |
| `get_active_python` | none | What `python` resolves to. |
| `switch_python` | `version`, `scope?` | Saves a PATH backup. |
| `list_available_pythons` | `series?`, `include_prereleases?` | Without `series`: one row per series. |
| `install_python` | `version`, `architecture?`, `activate?` | Downloads from python.org. |
| `uninstall_python` | `version` | Destructive. |
| `list_packages` | `python?`, `outdated_only?` | |
| `install_packages` | `packages[]`, `python?`, `upgrade?` | |
| `uninstall_packages` | `packages[]`, `python?` | Destructive. |
| `upgrade_packages` | `packages[]?`, `all?`, `python?` | |
| `package_info` | `name` | PyPI lookup. |
| `run_python` | `args[]`, `python?`, `cwd?`, `timeout_seconds?` | Captures stdout, stderr and the exit code. |
| `create_venv` | `directory`, `python?` | |
| `doctor` | `fix?` | PATH diagnostics. |

`python` takes the same selectors as the CLI's `-p` option and defaults to the active interpreter.

## Notes for assistants

- `pyvswitch use` changes the persistent PATH. A shell that is already open, including the one an
  agent is running in, keeps its old PATH. To use a version immediately, call
  `pyvswitch run -p <version> -- …` or take the path from `pyvswitch which -p <version>`.
- To give a project its own dependencies, prefer `pyvswitch venv .venv -p <version>` followed by
  `pyvswitch add … -p .venv` over installing into a shared interpreter.
- `pyvswitch uninstall` needs `--yes`. Ask the user before removing an interpreter.
- Nothing needs administrator rights unless `--scope system` or `--all-users` is used.
