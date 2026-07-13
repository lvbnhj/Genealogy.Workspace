#!/usr/bin/env bash
# install.sh — one-command local install for a fresh clone (macOS/Linux).
#
# Assumes Docker (with the `docker compose` plugin) and the .NET SDK are
# already installed. Does NOT install either of those.
#
# What it does:
#   1. Brings up PostgreSQL and applies all migrations (scripts/up.sh).
#   2. Builds and publishes Genealogy.Workspace.McpServer to
#      publish/GenealogyMcp/ (scripts/publish_mcp.sh). The generated run.sh
#      reads DB credentials from .env at launch time, so it never goes stale
#      and nothing machine-specific is baked into it.
#   3. Registers (or updates) a "genealogy-workspace" entry in this repo's
#      .mcp.json, pointing at THIS clone's run.sh path. Any other entries in
#      that file (e.g. a different MCP server) are left untouched.
#
# Safe to re-run: every step is idempotent.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"
MCP_JSON="${WORKSPACE_DIR}/.mcp.json"
RUN_SH="${WORKSPACE_DIR}/publish/GenealogyMcp/run.sh"

echo "== Genealogy.Workspace install =="
echo "Repo root: ${WORKSPACE_DIR}"
echo

# ---------------------------------------------------------------------------
# Step 0: prerequisite checks (fail fast with a clear message).
# ---------------------------------------------------------------------------
missing=()
command -v docker >/dev/null 2>&1 || missing+=("docker")
if command -v docker >/dev/null 2>&1 && ! docker compose version >/dev/null 2>&1; then
  missing+=("docker compose plugin")
fi
command -v dotnet >/dev/null 2>&1 || missing+=("dotnet SDK")

if [[ ${#missing[@]} -gt 0 ]]; then
  echo "Missing prerequisite(s): ${missing[*]}" >&2
  echo "Install them first, then re-run this script." >&2
  exit 1
fi
echo "[0/3] Prerequisites OK (docker, docker compose, dotnet)."

# ---------------------------------------------------------------------------
# Step 1: database + migrations.
# ---------------------------------------------------------------------------
echo
echo "[1/3] Starting PostgreSQL and applying migrations..."
"${SCRIPT_DIR}/up.sh"

# ---------------------------------------------------------------------------
# Step 2: build + publish the MCP server.
# ---------------------------------------------------------------------------
echo
echo "[2/3] Publishing the MCP server..."
"${SCRIPT_DIR}/publish_mcp.sh"

if [[ ! -x "${RUN_SH}" ]]; then
  echo "Expected ${RUN_SH} to exist after publishing but it does not." >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# Step 3: register the server in .mcp.json for this clone's absolute path.
# Uses python3 (already a project dependency for the GEDCOM tooling) for safe
# JSON merging; falls back to printing manual instructions if unavailable.
# ---------------------------------------------------------------------------
echo
echo "[3/3] Registering genealogy-workspace in ${MCP_JSON}..."

if command -v python3 >/dev/null 2>&1; then
  python3 - "$MCP_JSON" "$RUN_SH" <<'PYEOF'
import json
import sys
from pathlib import Path

mcp_json_path, run_sh_path = Path(sys.argv[1]), sys.argv[2]

config = {}
if mcp_json_path.exists():
    text = mcp_json_path.read_text(encoding="utf-8").strip()
    if text:
        config = json.loads(text)

config.setdefault("mcpServers", {})
config["mcpServers"]["genealogy-workspace"] = {"command": run_sh_path, "args": []}

mcp_json_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {mcp_json_path}")
PYEOF
else
  echo "python3 not found — could not update ${MCP_JSON} automatically." >&2
  echo "Add this manually to its \"mcpServers\" object:" >&2
  cat <<EOF
  "genealogy-workspace": {
    "command": "${RUN_SH}",
    "args": []
  }
EOF
fi

echo
echo "== Install complete =="
echo "MCP server entry point: ${RUN_SH}"
echo "Restart/reconnect your MCP client to pick up the \"genealogy-workspace\" server."
echo "Try it: ${SCRIPT_DIR}/quickstart.sh   (imports a sample tree + stores an evidence screenshot)"
