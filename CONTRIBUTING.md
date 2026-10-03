# Contributing

Thanks for taking the time to improve pyvswitch.

## Reporting a problem

Open an issue and include:

- The pyvswitch version (`pyvswitch --version`) and your Windows version.
- What you did, what you expected, and what happened instead.
- The output of `pyvswitch doctor` and `pyvswitch list` if the problem involves switching.
- The relevant part of `%APPDATA%\PythonVersionSwitch\logs\app.log`.

Review what you paste first: those outputs contain folder paths, which usually include your
Windows user name.

## Making a change

1. Fork the repository and create a branch.
2. Build and test:

   ```powershell
   dotnet build
   dotnet test
   ```

3. Keep the engine in `src/PyVSwitch.Core` and expose it through the CLI, MCP server and app
   rather than adding logic to one of them. [AGENTS.md](AGENTS.md) describes the layout and the
   conventions the code follows.
4. Add tests for new logic that can be tested without touching the real system (PATH editing,
   parsing, selector matching, config file edits).
5. For interface changes, render the pages and look at them in both themes:

   ```powershell
   .\src\PyVSwitch.App\bin\Debug\net9.0-windows\pyvswitchw.exe --demo --screenshots .\artifacts\shots
   .\src\PyVSwitch.App\bin\Debug\net9.0-windows\pyvswitchw.exe --demo --light --screenshots .\artifacts\shots
   ```

6. Open a pull request that says what changed and why.

## Safety expectations

pyvswitch edits the user's PATH and installs software, so changes in those areas need extra care:

- Every PATH change must be recorded as a backup that rollback can restore.
- Installers are downloaded only from python.org and verified before they run.
- Edits to other programs' configuration files (the AI integrations) must be confined to a marked
  block or a single key, be preceded by a backup, and be fully undone by `pyvswitch ai remove`.
