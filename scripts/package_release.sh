#!/usr/bin/env bash
# Produces a relocatable, framework-dependent release bundle. Building needs
# the .NET SDK; consuming the resulting archive needs only the .NET 10 Runtime.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"
VERSION="${1:-dev}"
SAFE_VERSION="${VERSION//\//-}"
ARTIFACTS_DIR="$WORKSPACE_DIR/artifacts"
BUNDLE_NAME="Genealogy.Workspace-${SAFE_VERSION}"
BUNDLE_DIR="$ARTIFACTS_DIR/$BUNDLE_NAME"

rm -rf "$BUNDLE_DIR"
mkdir -p "$BUNDLE_DIR/app/mcp" "$BUNDLE_DIR/app/migrator"

dotnet publish "$WORKSPACE_DIR/src/Genealogy.Workspace.McpServer/Genealogy.Workspace.McpServer.csproj" \
  -c Release --no-self-contained -p:UseAppHost=false \
  --output "$BUNDLE_DIR/app/mcp"

dotnet publish "$WORKSPACE_DIR/src/Genealogy.Workspace.Migrator/Genealogy.Workspace.Migrator.csproj" \
  -c Release --no-self-contained -p:UseAppHost=false \
  --output "$BUNDLE_DIR/app/migrator"

cp -R "$WORKSPACE_DIR/database" "$BUNDLE_DIR/database"
cp -R "$WORKSPACE_DIR/tools" "$BUNDLE_DIR/tools"
mkdir -p "$BUNDLE_DIR/scripts"
cp "$WORKSPACE_DIR/scripts/up.sh" "$BUNDLE_DIR/scripts/up.sh"
cp "$WORKSPACE_DIR/scripts/install-runtime.sh" "$BUNDLE_DIR/scripts/install-runtime.sh"
cp "$WORKSPACE_DIR/scripts/quickstart.sh" "$BUNDLE_DIR/scripts/quickstart.sh"
cp "$WORKSPACE_DIR/scripts/backup.sh" "$BUNDLE_DIR/scripts/backup.sh"
cp "$WORKSPACE_DIR/scripts/restore.sh" "$BUNDLE_DIR/scripts/restore.sh"
cp "$WORKSPACE_DIR/docker-compose.yml" "$BUNDLE_DIR/docker-compose.yml"
cp "$WORKSPACE_DIR/.env.example" "$BUNDLE_DIR/.env.example"
cp "$WORKSPACE_DIR/LICENSE" "$BUNDLE_DIR/LICENSE"
cp "$WORKSPACE_DIR/THIRD-PARTY-NOTICES.md" "$BUNDLE_DIR/THIRD-PARTY-NOTICES.md"
cp "$WORKSPACE_DIR/packaging/README-RUNTIME.md" "$BUNDLE_DIR/README.md"
cp "$WORKSPACE_DIR/packaging/run-mcp.sh" "$BUNDLE_DIR/run-mcp.sh"
cp "$WORKSPACE_DIR/packaging/run-mcp.ps1" "$BUNDLE_DIR/run-mcp.ps1"
cp "$WORKSPACE_DIR/packaging/start.ps1" "$BUNDLE_DIR/start.ps1"
printf '%s\n' "$VERSION" > "$BUNDLE_DIR/VERSION"

find "$BUNDLE_DIR" -type d -name __pycache__ -prune -exec rm -rf {} +
find "$BUNDLE_DIR/app" -type f -name '*.pdb' -delete
chmod +x "$BUNDLE_DIR/run-mcp.sh" "$BUNDLE_DIR/scripts/"*.sh

rm -f "$ARTIFACTS_DIR/$BUNDLE_NAME.tar.gz" "$ARTIFACTS_DIR/$BUNDLE_NAME.zip"
tar -C "$ARTIFACTS_DIR" -czf "$ARTIFACTS_DIR/$BUNDLE_NAME.tar.gz" "$BUNDLE_NAME"
(cd "$ARTIFACTS_DIR" && zip -qr "$BUNDLE_NAME.zip" "$BUNDLE_NAME")

# Stable aliases make /releases/latest/download/Genealogy.Workspace.tar.gz a
# permanent URL, while the versioned assets keep each release reproducible.
cp "$ARTIFACTS_DIR/$BUNDLE_NAME.tar.gz" "$ARTIFACTS_DIR/Genealogy.Workspace.tar.gz"
cp "$ARTIFACTS_DIR/$BUNDLE_NAME.zip" "$ARTIFACTS_DIR/Genealogy.Workspace.zip"
if command -v shasum >/dev/null 2>&1; then
  (cd "$ARTIFACTS_DIR" && shasum -a 256 \
    "$BUNDLE_NAME.tar.gz" "$BUNDLE_NAME.zip" \
    Genealogy.Workspace.tar.gz Genealogy.Workspace.zip > SHA256SUMS)
else
  (cd "$ARTIFACTS_DIR" && sha256sum \
    "$BUNDLE_NAME.tar.gz" "$BUNDLE_NAME.zip" \
    Genealogy.Workspace.tar.gz Genealogy.Workspace.zip > SHA256SUMS)
fi

echo "Created:"
echo "  $ARTIFACTS_DIR/$BUNDLE_NAME.tar.gz"
echo "  $ARTIFACTS_DIR/$BUNDLE_NAME.zip"
echo "  $ARTIFACTS_DIR/Genealogy.Workspace.tar.gz"
echo "  $ARTIFACTS_DIR/Genealogy.Workspace.zip"
echo "  $ARTIFACTS_DIR/SHA256SUMS"
