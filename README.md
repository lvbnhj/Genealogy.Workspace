# Genealogy.Workspace

Local PostgreSQL 17 workspace for the genealogy platform (see
`docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md`). This directory contains the
Docker Compose configuration, operational scripts, and (in `src/`) the .NET
projects.

## Runtime requirements

- Docker (with the `docker compose` plugin)
- .NET 10 Runtime (the SDK is not required)
- Host port 5432 free (configurable via `PGPORT` in `.env`)

Developing or building the repository additionally requires the .NET 10 SDK.

## Install with one command (prebuilt runtime, no source)

Create or enter an empty installation directory and pipe the bootstrap script
to Bash. It resolves the requested GitHub Release, downloads only the prebuilt
runtime bundle, starts PostgreSQL, applies migrations, and registers the MCP
server. It does not download repository source and does not require Git or the
.NET SDK.

```bash
mkdir -p ~/genealogy-workspace && cd ~/genealogy-workspace
curl -fsSL https://raw.githubusercontent.com/lvbnhj/Genealogy.Workspace/main/scripts/bootstrap.sh \
  | bash
```

The default writes `.mcp.json` in the installation directory. For Codex, write
a project-local `.codex/config.toml` instead:

```bash
curl -fsSL https://raw.githubusercontent.com/lvbnhj/Genealogy.Workspace/main/scripts/bootstrap.sh \
  | bash -s -- --client codex
```

To register Codex globally rather than only for this directory:

```bash
curl -fsSL https://raw.githubusercontent.com/lvbnhj/Genealogy.Workspace/main/scripts/bootstrap.sh \
  | bash -s -- --client codex --config "$HOME/.codex/config.toml"
```

Useful parameters are `--version <release-tag>`, `--install-dir <path>`,
`--config <path>`, `--name <server-name>`, `--port <host-port>`,
`--container-name <name>`, `--client none`, and `--no-start`.
Run the script with `--help` for the complete list. Re-running it in an existing
runtime directory upgrades the binaries and migrations while preserving
`.env`, backups, client configs, and the Docker volume.

> These commands pipe a script from the network into your shell. That is
> convenient but you are trusting the source — read
> [`scripts/bootstrap.sh`](scripts/bootstrap.sh) first if you prefer, or use the
> clone-based install below.

## Install from a fresh clone

On a development machine that has **Docker** and the **.NET SDK**, one script
does everything from a source checkout — brings up
PostgreSQL, applies migrations, builds and publishes the MCP server, and
registers it in this repo's `.mcp.json` with the correct absolute path for
*your* clone:

```bash
./scripts/install.sh          # macOS / Linux
```

```powershell
.\scripts\install.ps1         # Windows (PowerShell)
```

Both are safe to re-run (every step is idempotent) and never touch any other
entry already present in `.mcp.json`. After it finishes, restart/reconnect
your MCP client to pick up the `genealogy-workspace` server, then try
`./scripts/quickstart.sh` (below) to prove the whole pipeline end to end.

Neither source-install script installs Docker or the .NET SDK itself.

## Install / one-command start

`install.sh`/`install.ps1` above call this internally; run it directly if you
only want the database up (e.g. you already published the MCP server and are
iterating on the schema):

```bash
./scripts/up.sh
```

This will:

1. Create `Genealogy.Workspace/.env` from `.env.example` with a randomly
   generated password if it does not exist yet.
2. Start the PostgreSQL container (`postgres:17`, named `genealogy-postgres`
   by default — see `CONTAINER_NAME` under Configuration) and wait for its
   healthcheck to pass.
3. Run the numbered SQL migrations via
   `dotnet run --project src/Genealogy.Workspace.Migrator -- migrate`.

To start only the database without migrations:

```bash
docker compose up -d --wait
```

PostgreSQL listens on `127.0.0.1:5432` only — it is never exposed on external
interfaces (plan section 13).

## Quickstart: import a tree and add evidence

One command takes a clean machine from nothing to an imported family tree with
a stored evidence screenshot:

```bash
./scripts/quickstart.sh
```

This runs `up.sh` (starts PostgreSQL + applies migrations), then imports the
bundled sample GEDCOM (`tools/gedcom/tests/fixtures/phase0_baseline.ged`, 28
persons / 8 families) into a tree named `Quickstart`, and stores a sample
evidence screenshot as binary content in PostgreSQL. It prints a summary:

```
Summary:
  Tree:              Quickstart (<uuid>)
  Persons imported:  28
  Evidence record:   <uuid>
  Attachment hash:   <sha-256>
```

Options are forwarded to the importer:

```bash
./scripts/quickstart.sh --tree "My Tree" --ged /path/to/file.ged --screenshot /path/to/shot.png
```

If the database is already up (`./scripts/up.sh`) and `GENEALOGY_DB_*` are
exported, you can run the importer directly:

```bash
dotnet run --project src/Genealogy.Workspace.Migrator -- quickstart
```

The flow is non-interactive and safe to re-run: the tree is resolved-or-created,
and deterministic person UUIDs mean re-importing the same GEDCOM adds no
duplicate persons (attachments dedupe by content hash).

> If `up.sh`/migrations fail with `28P01: password authentication failed`, the
> Docker volume holds a password from a previous `.env`. Reset it with
> `docker compose down -v && docker compose up -d --wait`, then re-run.

## MCP server

Day-to-day use is through the stdio MCP server `Genealogy.Workspace.McpServer`,
which exposes 35 product-neutral tools (tree query, GEDCOM import, and the
Evidence Inbox) to an MCP client. `install.sh`/`install.ps1` (above) publish
and register it for you; to do just that step by hand:

```bash
./scripts/publish_mcp.sh
```

This publishes the server and generates a `run.sh` that reads DB credentials
from `Genealogy.Workspace/.env` at **launch time** (bridging `POSTGRES_*` ->
`GENEALOGY_DB_*`, exactly like `up.sh`) — nothing machine-specific is baked in,
so it never goes stale when `.env` changes, and an already-exported
`GENEALOGY_DB_*` value still overrides it. Add an entry to your MCP client's
`.mcp.json` pointing at that `run.sh` (the repository `.mcp.json` already has a
`genealogy-workspace` entry, kept up to date by the install scripts).

Once connected, drive it from your MCP client in natural language — the client
picks the right tool:

- *"List my trees"* → `list_tree_datasets`
- *"Find Тестенко in the Quickstart tree"* → `find_tree_person`
- *"Show Максим Іванович's ancestors, 4 generations"* → `get_ancestors`
- *"Closest common ancestor of Максим and Роксолана"* → `get_closest_common_ancestor`
- *"Stage /path/to/family.ged into the Rudenko tree, then show the preview"* →
  `stage_gedcom_import` + `get_gedcom_import_preview`
- *"Add a birth record with this scan and link the child to the tree"* →
  `add_research_record` + `add_research_attachment` + `suggest_record_person_links`

The full tool catalogue, attachment rules, and worked examples with real
request/response JSON are in [`docs/MCP.md`](docs/MCP.md).

## Configuration

Copy `.env.example` to `.env` and set a real password (or just run
`scripts/up.sh`, which does this for you). `.env` is gitignored; credentials
never enter source control, and logs never contain credentials.

| Variable | Default | Purpose |
|---|---|---|
| `POSTGRES_USER` | `genealogy` | Database superuser |
| `POSTGRES_PASSWORD` | — | Set a real value; generated by `up.sh` if missing |
| `POSTGRES_DB` | `genealogy_workspace` | Database name |
| `PGPORT` | `5432` | Host port bound on 127.0.0.1 |
| `CONTAINER_NAME` | `genealogy-postgres` | Docker container name |

### Running several installs on one machine

The defaults are fine for a single install. To run more than one independent
workspace on the same machine, give each clone its own `.env` with a **unique
`CONTAINER_NAME` and `PGPORT`** — e.g. `CONTAINER_NAME=genealogy-postgres-b`
and `PGPORT=5433` for the second one. The Docker volume is already isolated
per clone (Compose derives its name from the clone's directory), and all
scripts read `CONTAINER_NAME` from `.env`, so backup/restore/smoke target the
right container automatically.

## Upgrade

For a runtime installation, re-run the curl command from its directory. The
bootstrap resolves the latest release and preserves the database volume and
local configuration:

```bash
cd ~/genealogy-workspace
curl -fsSL https://raw.githubusercontent.com/lvbnhj/Genealogy.Workspace/main/scripts/bootstrap.sh \
  | bash -s -- --client codex
```

For a source checkout:

```bash
git pull
./scripts/up.sh
```

`up.sh` is idempotent: it reuses the running container (or pulls/starts a new
one if the image tag changed) and re-runs the migrator, which applies only
migrations not yet recorded in the database.

## Backup

```bash
./scripts/backup.sh
```

Writes a compressed custom-format `pg_dump` archive to
`Genealogy.Workspace/backups/genealogy_YYYYMMDD_HHMMSS.dump`. The `backups/`
directory is gitignored. Backups include all schemas in the database (both
`genealogy` and `research`, and therefore all attachment bytes).

## Restore

```bash
./scripts/restore.sh backups/genealogy_20260712_120000.dump
```

Restores with `--clean --if-exists` (drops and recreates objects present in
the dump). The script refuses to run without an explicit dump-file argument —
there is no implicit "latest backup" behavior and no automatic destructive
cleanup.

## Where the data lives

Database files are stored in the named Docker volume `genealogy_pgdata`
(mounted at `/var/lib/postgresql/data` in the container). The volume lives in
Docker's storage area, outside this Git checkout (plan section 13), so
checkouts, branch switches, and `git clean` never touch database data.

## Failure recovery

- **Container stopped or misbehaving:** `docker compose down` then
  `./scripts/up.sh`. `down` without `-v` keeps the `genealogy_pgdata` volume
  intact — no data is lost.
- **Full reset:** `docker compose down -v` deletes the volume; then run
  `./scripts/up.sh` (fresh empty database + migrations) and restore the latest
  backup with `./scripts/restore.sh <dump>`.
- **Machine reboot:** the container uses `restart: unless-stopped`, so Docker
  brings it back automatically.

## Release smoke test

```bash
./scripts/smoke.sh
```

This comprehensive test verifies that the entire release is ready:

1. Builds `Genealogy.Workspace.sln` in Release mode.
2. Starts PostgreSQL and applies all pending migrations.
3. Seeds a test record (`genealogy.smoke_seed`) with a timestamp.
4. Creates a compressed backup of the database.
5. Tampers with the database (deletes the seed record).
6. Restores the database from the backup.
7. Verifies that the restored record matches the original, and that both
   `genealogy` and `research` schemas exist with at least one applied migration.
8. Cleans up the test table and backup file.

The script exits with code 0 only if all steps pass. It demonstrates that
backup/restore actually restores data (Phase 1 exit criteria), and that the
complete build-migrations-verify pipeline is operational.

Run this before every release, and after pulling a new migration set.

## CI note

GitHub Actions runs the solution build, all 171 .NET integration tests, and all
13 GEDCOM Python tests for every pull request and push to `main`. Test results
are uploaded even when the test step fails. The CI PostgreSQL container uses a
run-specific name and port and is removed together with its volume at the end
of the job.

## Prebuilt release artifacts (Runtime only)

The **Build release bundle** GitHub Actions workflow can be started manually.
Pushing a `v*` tag also runs it and creates a GitHub Release automatically. It
creates `.tar.gz` and `.zip` bundles containing framework-dependent builds of
both the MCP server and Migrator, plus migrations, operational scripts, and the
GEDCOM parser. On a target machine these bundles require the **.NET 10
Runtime**, not the SDK.
Docker is still required for PostgreSQL; Python 3 is required only for GEDCOM
staging/import.

For a local package build:

```bash
./scripts/package_release.sh dev
```

Archives are written to `artifacts/`. A tag build creates a GitHub Release with
versioned archives, stable `Genealogy.Workspace.tar.gz` / `.zip` aliases for
`releases/latest/download`, and `SHA256SUMS`. A manually dispatched workflow
exposes them only as temporary workflow artifacts under the Actions run.

## Building this repository

This is a standalone product with its own solution — build and test with:

```bash
dotnet build Genealogy.Workspace.sln
```

`Genealogy.Workspace.sln` includes only `Genealogy.Workspace.Data`,
`.Migrator`, `.McpServer`, and the integration tests; none of them reference
any DNA, SQL Server, or legacy-database code. `scripts/smoke.sh` builds this
solution, so a release never requires anything outside this repository to
compile.

## Security notes

- Database is bound to `127.0.0.1` only.
- Credentials live only in the untracked `.env` file.
- Logs (container and scripts) never contain credentials.
- Uploaded attachment content is never executed; its MIME type is verified from
  magic bytes (allowlist: PNG/JPEG/GIF/WEBP/TIFF/PDF), not the client-declared
  type, and per-file/per-record size limits are enforced.

## License

MIT — see [`LICENSE`](LICENSE). Third-party components and their licenses are
listed in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
