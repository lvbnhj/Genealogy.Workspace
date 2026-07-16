#!/usr/bin/env bash
# One-command start: bring up PostgreSQL (creating .env if missing) and apply migrations.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"

ENV_FILE="$WORKSPACE_DIR/.env"
ENV_EXAMPLE="$WORKSPACE_DIR/.env.example"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "No .env found — creating one from .env.example with a generated password."
  PASSWORD="$(openssl rand -hex 16)"
  sed "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=${PASSWORD}/" "$ENV_EXAMPLE" > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  echo "Wrote $ENV_FILE (password generated; not committed to git)."
fi

# shellcheck disable=SC1090
set -a
source "$ENV_FILE"
set +a

echo "Starting PostgreSQL..."
docker compose --project-directory "$WORKSPACE_DIR" up -d --wait

echo "Applying migrations..."
# Map the compose-style variables in .env to the environment names the
# migrator reads (GENEALOGY_DB_*).
export GENEALOGY_DB_HOST=127.0.0.1
export GENEALOGY_DB_PORT="$PGPORT"
export GENEALOGY_DB_DATABASE="$POSTGRES_DB"
export GENEALOGY_DB_USERNAME="$POSTGRES_USER"
export GENEALOGY_DB_PASSWORD="$POSTGRES_PASSWORD"
PUBLISHED_MIGRATOR="$WORKSPACE_DIR/app/migrator/Genealogy.Workspace.Migrator.dll"
if [[ -f "$PUBLISHED_MIGRATOR" ]]; then
  dotnet "$PUBLISHED_MIGRATOR" migrate
else
  dotnet run --project "$WORKSPACE_DIR/src/Genealogy.Workspace.Migrator" -- migrate
fi

echo "Genealogy workspace is up."
