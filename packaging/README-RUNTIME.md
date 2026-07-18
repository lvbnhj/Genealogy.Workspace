# Genealogy.Workspace runtime bundle

This bundle is framework-dependent: it requires the **.NET 10 Runtime**, not
the .NET SDK. Docker is required for the bundled PostgreSQL service. Python 3
is additionally required only when staging/importing GEDCOM files.

## Installed with the bootstrap script

The recommended installer downloads this prebuilt bundle, starts the database,
applies migrations, and registers `run-mcp.sh` in `.mcp.json` or Codex
`config.toml`. No source checkout or .NET SDK is used.

## Manual first start

1. Run the bundled installer. It creates `.env` with a random password unless
   one already exists, then starts PostgreSQL and applies migrations:

   ```bash
   ./scripts/install-runtime.sh --client mcp-json
   ```

   For Codex project configuration, use `--client codex`; for the global Codex
   config, add `--config "$HOME/.codex/config.toml"`.

2. On Windows, manually copy `.env.example` to `.env`, replace `CHANGE_ME`, and
   start the database:

   ```powershell
   .\start.ps1
   ```

3. Run the MCP server with `./run-mcp.sh` or `.\run-mcp.ps1`.

The release contains migrations and the GEDCOM parser as ordinary files so the
published Migrator can discover them without a source checkout or SDK.
