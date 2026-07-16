# Genealogy.Workspace runtime bundle

This bundle is framework-dependent: it requires the **.NET 10 Runtime**, not
the .NET SDK. Docker is required for the bundled PostgreSQL service. Python 3
is additionally required only when staging/importing GEDCOM files.

## First start

1. Copy `.env.example` to `.env` and replace `CHANGE_ME` with a real password.
2. Start PostgreSQL and apply migrations:

   ```bash
   ./scripts/up.sh
   ```

   On Windows:

   ```powershell
   .\start.ps1
   ```

3. Run the MCP server with `./run-mcp.sh` or `.\run-mcp.ps1`.

The release contains migrations and the GEDCOM parser as ordinary files so the
published Migrator can discover them without a source checkout or SDK.
