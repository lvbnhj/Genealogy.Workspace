#!/usr/bin/env bash
# publish_mcp.sh — builds and publishes Genealogy.Workspace.McpServer to
# publish/GenealogyMcp inside this repo.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"

CSPROJ="${WORKSPACE_DIR}/src/Genealogy.Workspace.McpServer/Genealogy.Workspace.McpServer.csproj"
OUT_DIR="${WORKSPACE_DIR}/publish/GenealogyMcp"
DOTNET="${DOTNET:-$(command -v dotnet)}"

if [[ -z "${DOTNET}" ]]; then
  echo "dotnet not found in PATH" >&2
  exit 1
fi

if [[ ! -f "${CSPROJ}" ]]; then
  echo ".csproj not found at ${CSPROJ}" >&2
  exit 1
fi

echo "dotnet: $(${DOTNET} --version)"
echo "Publishing Genealogy.Workspace.McpServer -> ${OUT_DIR}"

"${DOTNET}" publish "${CSPROJ}" \
  -c Release \
  --no-self-contained \
  --output "${OUT_DIR}"

# Ensure run.sh wrapper exists and is executable. It reads DB credentials
# from Genealogy.Workspace/.env at LAUNCH time (not baked in at publish time),
# so regenerating .env (a new password, a different port) never requires
# republishing. An already-exported GENEALOGY_DB_* value in the parent
# shell/MCP client config still wins over .env, matching up.sh's convention.
cat > "${OUT_DIR}/run.sh" <<EOF
#!/bin/sh
# Runs the published Genealogy.Workspace.McpServer over stdio.
# Reads DB credentials from Genealogy.Workspace/.env (same file up.sh
# creates/uses), bridging POSTGRES_* -> GENEALOGY_DB_* exactly like up.sh.
# Regenerate this file by re-running publish_mcp.sh if it is ever deleted;
# it does not need to change when .env changes.
ENV_FILE="${WORKSPACE_DIR}/.env"
if [ -f "\${ENV_FILE}" ]; then
  set -a
  # shellcheck disable=SC1090
  . "\${ENV_FILE}"
  set +a
fi

: "\${GENEALOGY_DB_HOST:=127.0.0.1}"
: "\${GENEALOGY_DB_PORT:=\${PGPORT:-5432}}"
: "\${GENEALOGY_DB_DATABASE:=\${POSTGRES_DB:-genealogy_workspace}}"
: "\${GENEALOGY_DB_USERNAME:=\${POSTGRES_USER:-genealogy}}"
: "\${GENEALOGY_DB_PASSWORD:=\${POSTGRES_PASSWORD:-}}"
if [ -z "\${GENEALOGY_DB_PASSWORD}" ]; then
  echo "GENEALOGY_DB_PASSWORD is not set and \${ENV_FILE} has no POSTGRES_PASSWORD. Run scripts/up.sh once to create .env, or export GENEALOGY_DB_PASSWORD yourself." >&2
  exit 1
fi
export GENEALOGY_DB_HOST GENEALOGY_DB_PORT GENEALOGY_DB_DATABASE GENEALOGY_DB_USERNAME GENEALOGY_DB_PASSWORD

DOTNET_BIN="\$(command -v dotnet || echo /opt/homebrew/bin/dotnet)"
exec "\${DOTNET_BIN}" "\$(dirname "\$0")/Genealogy.Workspace.McpServer.dll" "\$@"
EOF
chmod +x "${OUT_DIR}/run.sh"

echo "Done. Entry point: ${OUT_DIR}/run.sh"
echo "It reads DB credentials from ${WORKSPACE_DIR}/.env automatically (run scripts/up.sh first if that file does not exist yet)."
