#!/usr/bin/env bash
# Finish setup of an already-extracted runtime bundle.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
CLIENT="mcp-json"
CONFIG_PATH=""
SERVER_NAME="genealogy-workspace"
START_WORKSPACE=1
PG_PORT=""
CONTAINER_NAME_OVERRIDE=""

usage() {
  cat <<'EOF'
Usage: install-runtime.sh [options]
  --client <mcp-json|codex|none>
  --config <path>
  --name <server-name>
  --port <host-port>
  --container-name <name>
  --no-start
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --client) [[ $# -ge 2 ]] || { echo "--client requires a value." >&2; exit 2; }; CLIENT="$2"; shift 2 ;;
    --config) [[ $# -ge 2 ]] || { echo "--config requires a value." >&2; exit 2; }; CONFIG_PATH="$2"; shift 2 ;;
    --name) [[ $# -ge 2 ]] || { echo "--name requires a value." >&2; exit 2; }; SERVER_NAME="$2"; shift 2 ;;
    --port) [[ $# -ge 2 ]] || { echo "--port requires a value." >&2; exit 2; }; PG_PORT="$2"; shift 2 ;;
    --container-name) [[ $# -ge 2 ]] || { echo "--container-name requires a value." >&2; exit 2; }; CONTAINER_NAME_OVERRIDE="$2"; shift 2 ;;
    --no-start) START_WORKSPACE=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$CLIENT" in
  mcp-json|codex|none) ;;
  *) echo "--client must be mcp-json, codex, or none (got '$CLIENT')." >&2; exit 2 ;;
esac

if [[ -n "$PG_PORT" && ! "$PG_PORT" =~ ^[0-9]+$ ]]; then
  echo "--port must be numeric (got '$PG_PORT')." >&2
  exit 2
fi
if [[ -n "$CONTAINER_NAME_OVERRIDE" && ! "$CONTAINER_NAME_OVERRIDE" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]]; then
  echo "--container-name contains invalid Docker name characters." >&2
  exit 2
fi

missing=()
command -v dotnet >/dev/null 2>&1 || missing+=(".NET 10 Runtime")
if [[ "$START_WORKSPACE" == "1" ]]; then
  command -v docker >/dev/null 2>&1 || missing+=("Docker")
  if command -v docker >/dev/null 2>&1 && ! docker compose version >/dev/null 2>&1; then
    missing+=("Docker Compose plugin")
  fi
fi
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "Missing prerequisite(s): ${missing[*]}" >&2
  exit 1
fi

if ! dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 10\.'; then
  echo ".NET 10 Runtime is required. The .NET SDK is not required." >&2
  exit 1
fi

ENV_FILE="$ROOT_DIR/.env"
if [[ -n "$PG_PORT" || -n "$CONTAINER_NAME_OVERRIDE" ]]; then
  if [[ ! -f "$ENV_FILE" ]]; then
    if command -v openssl >/dev/null 2>&1; then
      generated_password="$(openssl rand -hex 16)"
    else
      generated_password="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
    fi
    sed "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=${generated_password}/" \
      "$ROOT_DIR/.env.example" > "$ENV_FILE"
    chmod 600 "$ENV_FILE"
  fi

  update_env_value() {
    local key="$1"
    local value="$2"
    local temp_file="${ENV_FILE}.tmp.$$"
    awk -v key="$key" -v value="$value" '
      BEGIN { replaced = 0 }
      index($0, key "=") == 1 { print key "=" value; replaced = 1; next }
      { print }
      END { if (!replaced) print key "=" value }
    ' "$ENV_FILE" > "$temp_file"
    mv "$temp_file" "$ENV_FILE"
    chmod 600 "$ENV_FILE"
  }

  if [[ -n "$PG_PORT" ]]; then
    update_env_value PGPORT "$PG_PORT"
  fi
  if [[ -n "$CONTAINER_NAME_OVERRIDE" ]]; then
    update_env_value CONTAINER_NAME "$CONTAINER_NAME_OVERRIDE"
  fi
fi

if [[ "$START_WORKSPACE" == "1" ]]; then
  echo "Starting PostgreSQL and applying migrations..."
  "$SCRIPT_DIR/up.sh"
fi

if [[ "$CLIENT" != "none" ]]; then
  if [[ -z "$CONFIG_PATH" ]]; then
    if [[ "$CLIENT" == "codex" ]]; then
      CONFIG_PATH="$ROOT_DIR/.codex/config.toml"
    else
      CONFIG_PATH="$ROOT_DIR/.mcp.json"
    fi
  fi

  dotnet "$ROOT_DIR/app/migrator/Genealogy.Workspace.Migrator.dll" register-mcp \
    --client "$CLIENT" \
    --config "$CONFIG_PATH" \
    --command "$ROOT_DIR/run-mcp.sh" \
    --name "$SERVER_NAME"
fi

echo
echo "Genealogy.Workspace runtime installation complete."
echo "Runtime: $ROOT_DIR"
echo "MCP entry point: $ROOT_DIR/run-mcp.sh"
if [[ "$CLIENT" != "none" ]]; then
  echo "Client config: $CONFIG_PATH"
  echo "Restart or reconnect the MCP client to load '$SERVER_NAME'."
fi
