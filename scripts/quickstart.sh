#!/usr/bin/env bash
# Phase 7 quickstart: from a clean machine, bring up PostgreSQL + migrations,
# then import a sample GEDCOM into a tree and store an evidence screenshot.
# Non-interactive and safe to re-run. Extra args are forwarded to the migrator's
# `quickstart` command, e.g.:
#   ./scripts/quickstart.sh --tree "My Tree" --ged /path/to/file.ged --screenshot shot.png
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"

# Step 1: bring up the database and apply migrations (creates .env if missing).
echo "Bringing up PostgreSQL and applying migrations..."
"$SCRIPT_DIR/up.sh"

# Load the compose-style variables so we can bridge them to GENEALOGY_DB_*,
# exactly as up.sh does (the .env is guaranteed to exist after up.sh ran).
ENV_FILE="$WORKSPACE_DIR/.env"
# shellcheck disable=SC1090
set -a
source "$ENV_FILE"
set +a

export GENEALOGY_DB_HOST=127.0.0.1
export GENEALOGY_DB_PORT="$PGPORT"
export GENEALOGY_DB_DATABASE="$POSTGRES_DB"
export GENEALOGY_DB_USERNAME="$POSTGRES_USER"
export GENEALOGY_DB_PASSWORD="$POSTGRES_PASSWORD"

# Step 2: run the end-to-end quickstart, forwarding any extra args.
echo "Running quickstart (import GEDCOM + add evidence screenshot)..."
dotnet run --project "$WORKSPACE_DIR/src/Genealogy.Workspace.Migrator" -- quickstart "$@"

echo "Quickstart finished. A sample GEDCOM was imported and an evidence screenshot stored."
