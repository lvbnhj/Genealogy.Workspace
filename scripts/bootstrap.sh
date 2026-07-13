#!/usr/bin/env bash
# bootstrap.sh — download the Genealogy.Workspace source and install it, WITHOUT
# needing git. Intended to be run straight from the network:
#
#   curl -fsSL https://raw.githubusercontent.com/lvbnhj/Genealogy.Workspace/main/scripts/bootstrap.sh | bash
#
# It downloads a source tarball from GitHub, extracts it, and runs
# scripts/install.sh (which brings up PostgreSQL, applies migrations, publishes
# the MCP server, and registers it in .mcp.json). Only `curl` and `tar` are
# required for this step; install.sh then checks for Docker and the .NET SDK.
#
# Configuration (all optional, via environment variables):
#   REPO         GitHub "owner/repo"          (default: lvbnhj/Genealogy.Workspace)
#   REF          branch or tag to download    (default: main)
#   TARGET_DIR   directory to extract into    (default: genealogy-workspace)
#   GITHUB_TOKEN token for a PRIVATE repo     (default: none — anonymous, public repo)
#   TARBALL_URL  override the download URL     (default: GitHub tarball for REPO@REF)
#   RUN_INSTALL  set to 0 to only download/extract and skip install (default: 1)
#
# Examples:
#   REPO=alice/Genealogy.Workspace bash bootstrap.sh
#   REPO=alice/Genealogy.Workspace REF=v1.0 TARGET_DIR=~/genealogy bash bootstrap.sh
#   GITHUB_TOKEN=ghp_xxx REPO=alice/private-genealogy bash bootstrap.sh
set -euo pipefail

REPO="${REPO:-lvbnhj/Genealogy.Workspace}"
REF="${REF:-main}"
TARGET_DIR="${TARGET_DIR:-genealogy-workspace}"
RUN_INSTALL="${RUN_INSTALL:-1}"
TARBALL_URL="${TARBALL_URL:-https://api.github.com/repos/${REPO}/tarball/${REF}}"

echo "== Genealogy.Workspace bootstrap =="

# --- prerequisites for THIS script (install.sh checks Docker/.NET later) -------
missing=()
command -v curl >/dev/null 2>&1 || missing+=("curl")
command -v tar  >/dev/null 2>&1 || missing+=("tar")
if [ ${#missing[@]} -gt 0 ]; then
  echo "Missing required tool(s): ${missing[*]}" >&2
  exit 1
fi

# --- refuse to clobber a non-empty target -------------------------------------
if [ -e "$TARGET_DIR" ] && [ -n "$(ls -A "$TARGET_DIR" 2>/dev/null || true)" ]; then
  echo "Target directory '$TARGET_DIR' exists and is not empty." >&2
  echo "Set TARGET_DIR=<dir> or remove/empty it, then re-run." >&2
  exit 1
fi
mkdir -p "$TARGET_DIR"

# --- download + extract (strip GitHub's top-level <owner>-<repo>-<sha>/ folder) -
auth=()
if [ -n "${GITHUB_TOKEN:-}" ]; then
  auth=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
fi

echo "Downloading ${REPO}@${REF} ..."
if ! curl -fsSL "${auth[@]}" "$TARBALL_URL" | tar -xz --strip-components=1 -C "$TARGET_DIR"; then
  echo "Download/extract failed for ${TARBALL_URL}." >&2
  echo "Check REPO/REF, that the repo is public (or GITHUB_TOKEN is set for a private repo)," >&2
  echo "and your network connection." >&2
  exit 1
fi

ABS_TARGET="$(cd "$TARGET_DIR" && pwd)"
echo "Extracted into ${ABS_TARGET}"

if [ ! -x "${ABS_TARGET}/scripts/install.sh" ]; then
  echo "Extracted tree has no scripts/install.sh — is REPO the Genealogy.Workspace repo?" >&2
  exit 1
fi

if [ "$RUN_INSTALL" != "1" ]; then
  echo "RUN_INSTALL=0 — skipping install. Next: cd ${ABS_TARGET} && ./scripts/install.sh"
  exit 0
fi

echo "Running install..."
cd "$ABS_TARGET"
exec ./scripts/install.sh
