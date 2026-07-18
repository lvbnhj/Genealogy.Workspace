#!/usr/bin/env bash
# Download a prebuilt GitHub Release, start PostgreSQL, apply migrations, and
# register the MCP server. Repository source and the .NET SDK are not needed.
#
#   curl -fsSL https://raw.githubusercontent.com/lvbnhj/Genealogy.Workspace/main/scripts/bootstrap.sh \
#     | bash -s -- --client codex
set -euo pipefail

REPO="${REPO:-lvbnhj/Genealogy.Workspace}"
VERSION="${VERSION:-latest}"
INSTALL_DIR="${INSTALL_DIR:-.}"
CLIENT="${CLIENT:-mcp-json}"
CONFIG_PATH="${CONFIG_PATH:-}"
SERVER_NAME="${SERVER_NAME:-genealogy-workspace}"
ASSET_URL="${ASSET_URL:-}"
PG_PORT="${PG_PORT:-}"
CONTAINER_NAME_OVERRIDE="${CONTAINER_NAME_OVERRIDE:-}"
START_WORKSPACE=1
FORCE=0

usage() {
  cat <<'EOF'
Install the prebuilt Genealogy.Workspace runtime (no source checkout, no SDK).

Usage: bootstrap.sh [options]

  --client <mcp-json|codex|none>
                              Client config format (default: mcp-json)
  --config <path>             Config to update. Defaults to ./.mcp.json, or
                              ./.codex/config.toml for --client codex
  --install-dir <path>        Runtime destination (default: current directory)
  --version <tag|latest>      GitHub Release tag (default: latest)
  --name <server-name>        MCP server name (default: genealogy-workspace)
  --port <host-port>          Override PostgreSQL host port in .env
  --container-name <name>     Override Docker container name in .env
  --asset-url <url>           Override archive URL (testing/mirrors)
  --no-start                  Download/configure without starting PostgreSQL
  --force                     Allow an unrelated non-empty target directory
  -h, --help                  Show this help

Examples:
  curl -fsSL <bootstrap-url> | bash
  curl -fsSL <bootstrap-url> | bash -s -- --client codex
  curl -fsSL <bootstrap-url> | bash -s -- --client codex \
    --config "$HOME/.codex/config.toml"
EOF
}

require_value() {
  if [[ $# -lt 2 || -z "$2" ]]; then
    echo "$1 requires a value." >&2
    exit 2
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --client) require_value "$@"; CLIENT="$2"; shift 2 ;;
    --config) require_value "$@"; CONFIG_PATH="$2"; shift 2 ;;
    --install-dir) require_value "$@"; INSTALL_DIR="$2"; shift 2 ;;
    --version) require_value "$@"; VERSION="$2"; shift 2 ;;
    --name) require_value "$@"; SERVER_NAME="$2"; shift 2 ;;
    --port) require_value "$@"; PG_PORT="$2"; shift 2 ;;
    --container-name) require_value "$@"; CONTAINER_NAME_OVERRIDE="$2"; shift 2 ;;
    --asset-url) require_value "$@"; ASSET_URL="$2"; shift 2 ;;
    --no-start) START_WORKSPACE=0; shift ;;
    --force) FORCE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$CLIENT" in
  mcp-json|codex|none) ;;
  *) echo "--client must be mcp-json, codex, or none (got '$CLIENT')." >&2; exit 2 ;;
esac

missing=()
command -v curl >/dev/null 2>&1 || missing+=("curl")
command -v tar >/dev/null 2>&1 || missing+=("tar")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "Missing required tool(s): ${missing[*]}" >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
ABS_TARGET="$(cd "$INSTALL_DIR" && pwd)"

if [[ -n "$(ls -A "$ABS_TARGET" 2>/dev/null || true)" ]] \
  && [[ ! -f "$ABS_TARGET/VERSION" || ! -d "$ABS_TARGET/app/mcp" ]] \
  && [[ "$FORCE" != "1" ]]; then
  echo "Target is non-empty and is not a recognized runtime: $ABS_TARGET" >&2
  echo "Use an empty directory, an existing runtime, or --force." >&2
  exit 1
fi

curl_args=(-fsSL --retry 3 --retry-delay 1)
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  curl_args+=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
fi

if [[ -z "$ASSET_URL" ]]; then
  if [[ "$VERSION" == "latest" ]]; then
    ASSET_URL="https://github.com/${REPO}/releases/latest/download/Genealogy.Workspace.tar.gz"
  else
    ASSET_URL="https://github.com/${REPO}/releases/download/${VERSION}/Genealogy.Workspace-${VERSION}.tar.gz"
  fi
fi

TEMP_DIR="$(mktemp -d)"
cleanup() { rm -rf "$TEMP_DIR"; }
trap cleanup EXIT
ARCHIVE="$TEMP_DIR/runtime.tar.gz"
EXTRACT_DIR="$TEMP_DIR/extracted"
mkdir -p "$EXTRACT_DIR"

echo "Downloading Genealogy.Workspace ${VERSION} runtime..."
curl "${curl_args[@]}" "$ASSET_URL" -o "$ARCHIVE"

archive_members="$(tar -tzf "$ARCHIVE")"
while IFS= read -r member; do
  case "$member" in
    /*|../*|*/../*|*/..)
      echo "Archive contains an unsafe path: $member" >&2
      exit 1
      ;;
  esac
done <<< "$archive_members"

tar -xzf "$ARCHIVE" -C "$EXTRACT_DIR"

BUNDLE_DIR="$(find "$EXTRACT_DIR" -mindepth 1 -maxdepth 1 -type d -print | head -n 1)"
if [[ -z "$BUNDLE_DIR" \
  || ! -f "$BUNDLE_DIR/app/mcp/Genealogy.Workspace.McpServer.dll" \
  || ! -f "$BUNDLE_DIR/app/migrator/Genealogy.Workspace.Migrator.dll" \
  || ! -x "$BUNDLE_DIR/scripts/install-runtime.sh" \
  || ! -x "$BUNDLE_DIR/run-mcp.sh" ]]; then
  echo "Downloaded archive is not a valid Genealogy.Workspace runtime bundle." >&2
  exit 1
fi

# Replace release-owned payload only. Preserve .env, backups, client configs,
# and the Docker volume so installing a newer release is safe and idempotent.
managed_entries=(
  app database tools scripts docker-compose.yml .env.example LICENSE
  THIRD-PARTY-NOTICES.md README.md run-mcp.sh run-mcp.ps1 start.ps1 VERSION
)
for entry in "${managed_entries[@]}"; do
  rm -rf "$ABS_TARGET/$entry"
done
cp -R "$BUNDLE_DIR"/. "$ABS_TARGET"/

echo "Installed runtime into $ABS_TARGET"

install_args=(--client "$CLIENT" --name "$SERVER_NAME")
if [[ -n "$CONFIG_PATH" ]]; then
  install_args+=(--config "$CONFIG_PATH")
fi
if [[ -n "$PG_PORT" ]]; then
  install_args+=(--port "$PG_PORT")
fi
if [[ -n "$CONTAINER_NAME_OVERRIDE" ]]; then
  install_args+=(--container-name "$CONTAINER_NAME_OVERRIDE")
fi
if [[ "$START_WORKSPACE" != "1" ]]; then
  install_args+=(--no-start)
fi

exec "$ABS_TARGET/scripts/install-runtime.sh" "${install_args[@]}"
