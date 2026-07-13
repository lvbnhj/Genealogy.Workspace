#!/usr/bin/env bash
# Create a compressed (custom-format) pg_dump backup of the genealogy database.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"

ENV_FILE="$WORKSPACE_DIR/.env"
if [[ ! -f "$ENV_FILE" ]]; then
  echo "Error: $ENV_FILE not found. Run scripts/up.sh first." >&2
  exit 1
fi

# shellcheck disable=SC1090
set -a
source "$ENV_FILE"
set +a

BACKUP_DIR="$WORKSPACE_DIR/backups"
mkdir -p "$BACKUP_DIR"

BACKUP_FILE="$BACKUP_DIR/genealogy_$(date +%Y%m%d_%H%M%S).dump"

docker exec "${CONTAINER_NAME:-genealogy-postgres}" pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc > "$BACKUP_FILE"

echo "Backup written: $BACKUP_FILE"
