#!/usr/bin/env sh
set -eu

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
ENV_FILE="$ROOT_DIR/.env"

if [ -f "$ENV_FILE" ]; then
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
fi

: "${GENEALOGY_DB_HOST:=127.0.0.1}"
: "${GENEALOGY_DB_PORT:=${PGPORT:-5432}}"
: "${GENEALOGY_DB_DATABASE:=${POSTGRES_DB:-genealogy_workspace}}"
: "${GENEALOGY_DB_USERNAME:=${POSTGRES_USER:-genealogy}}"
: "${GENEALOGY_DB_PASSWORD:=${POSTGRES_PASSWORD:-}}"

if [ -z "$GENEALOGY_DB_PASSWORD" ]; then
  echo "Database password is missing. Copy .env.example to .env and set POSTGRES_PASSWORD." >&2
  exit 1
fi

export GENEALOGY_DB_HOST GENEALOGY_DB_PORT GENEALOGY_DB_DATABASE
export GENEALOGY_DB_USERNAME GENEALOGY_DB_PASSWORD

exec dotnet "$ROOT_DIR/app/mcp/Genealogy.Workspace.McpServer.dll" "$@"

