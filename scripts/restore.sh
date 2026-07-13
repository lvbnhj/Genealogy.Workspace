#!/usr/bin/env bash
# Restore the genealogy database from a pg_dump custom-format dump.
# Usage: scripts/restore.sh <path-to-dump-file>
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"

if [[ $# -lt 1 || -z "${1:-}" ]]; then
  echo "Error: dump file argument required." >&2
  echo "Usage: $0 <path-to-dump-file>" >&2
  exit 1
fi

DUMP_FILE="$1"
if [[ ! -f "$DUMP_FILE" ]]; then
  echo "Error: dump file not found: $DUMP_FILE" >&2
  exit 1
fi

ENV_FILE="$WORKSPACE_DIR/.env"
if [[ ! -f "$ENV_FILE" ]]; then
  echo "Error: $ENV_FILE not found. Run scripts/up.sh first." >&2
  exit 1
fi

# shellcheck disable=SC1090
set -a
source "$ENV_FILE"
set +a

echo "Restoring $DUMP_FILE into database '$POSTGRES_DB'..."
docker exec -i "${CONTAINER_NAME:-genealogy-postgres}" pg_restore \
  -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  --clean --if-exists < "$DUMP_FILE"

echo "Restore complete."
