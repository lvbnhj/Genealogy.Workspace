#!/usr/bin/env bash
# Release smoke test: builds solution, starts PostgreSQL, applies migrations,
# seeds a test record, backs up, tampers, restores, and verifies.
# Exit code 0 = all steps pass; nonzero = failure.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(dirname "$SCRIPT_DIR")"

ENV_FILE="$WORKSPACE_DIR/.env"

# ============================================================================
# STEP 1: Build solution
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 1: Build the standalone Genealogy.Workspace.sln in Release mode — no DNA/SQL Server dependency"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

cd "$WORKSPACE_DIR"
if dotnet build Genealogy.Workspace.sln -c Release > /dev/null 2>&1; then
  echo "✓ PASS"
else
  echo "✗ FAIL: dotnet build failed"
  exit 1
fi

# ============================================================================
# STEP 2: Start PostgreSQL and apply migrations
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 2: Start PostgreSQL and apply migrations (scripts/up.sh)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

cd "$WORKSPACE_DIR"
if bash "$SCRIPT_DIR/up.sh" > /dev/null 2>&1; then
  echo "✓ PASS"
else
  echo "✗ FAIL: scripts/up.sh failed"
  exit 1
fi

# Source .env to get credentials
if [[ ! -f "$ENV_FILE" ]]; then
  echo "✗ FAIL: .env not found after up.sh"
  exit 1
fi

set -a
source "$ENV_FILE"
set +a

# Container name (defaults to genealogy-postgres; overridable via .env for
# side-by-side installs).
CONTAINER_NAME="${CONTAINER_NAME:-genealogy-postgres}"

# ============================================================================
# STEP 3: Create and seed smoke test table
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 3: Seed test record (genealogy.smoke_seed)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

SEED_TIMESTAMP=$(date +%s)
SEED_NOTE="smoke_${SEED_TIMESTAMP}"

docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "CREATE TABLE IF NOT EXISTS genealogy.smoke_seed(id int primary key, note text);" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to create smoke_seed table"
  exit 1
}

docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "INSERT INTO genealogy.smoke_seed(id, note) VALUES(1, '$SEED_NOTE');" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to insert seed record"
  exit 1
}

echo "✓ PASS: Seed note = '$SEED_NOTE'"

# ----------------------------------------------------------------------------
# Evidence Inbox attachment seed (Phase 6 Task 5 exit criterion, plan §10
# step 8): seeds one research.source_record + one research.attachment_content
# row with a known small PNG byte sequence, so STEP 7 below can assert both
# the record and the attachment bytes survive the backup -> tamper -> restore
# cycle byte-identical. Mirrors EvidenceInboxExitScenarioTests.MakePng's
# 8-byte PNG signature (the rest of the bytes here are just filler).
# ----------------------------------------------------------------------------
ATTACH_TREE_ID=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "INSERT INTO genealogy.tree (tree_id, name, is_default) VALUES (gen_random_uuid(), 'Smoke Test Tree', false) RETURNING tree_id;" 2>/dev/null | head -1 | tr -d ' ')

if [[ -z "$ATTACH_TREE_ID" ]]; then
  echo "✗ FAIL: failed to seed genealogy.tree for attachment smoke test"
  exit 1
fi

ATTACH_RECORD_ID=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "INSERT INTO research.source_record (source_record_id, tree_id, title, record_type)
   VALUES (gen_random_uuid(), '$ATTACH_TREE_ID', 'Smoke test evidence record', 'other')
   RETURNING source_record_id;" 2>/dev/null | head -1 | tr -d ' ')

if [[ -z "$ATTACH_RECORD_ID" ]]; then
  echo "✗ FAIL: failed to seed research.source_record"
  exit 1
fi

# Known small PNG byte sequence: the 8-byte PNG signature plus 8 filler bytes.
ATTACH_PNG_HEX="89504e470d0a1a0a1122334455667788"

ATTACH_CONTENT_ID=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "INSERT INTO research.attachment_content (content_hash, content, byte_length, mime_type)
   VALUES (encode(sha256(decode('$ATTACH_PNG_HEX','hex')), 'hex'), decode('$ATTACH_PNG_HEX','hex'),
           length(decode('$ATTACH_PNG_HEX','hex')), 'image/png')
   RETURNING attachment_content_id;" 2>/dev/null | head -1 | tr -d ' ')

if [[ -z "$ATTACH_CONTENT_ID" ]]; then
  echo "✗ FAIL: failed to seed research.attachment_content"
  exit 1
fi

docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "INSERT INTO research.source_record_attachment (source_record_id, attachment_content_id, file_name, attachment_type)
   VALUES ('$ATTACH_RECORD_ID', $ATTACH_CONTENT_ID, 'smoke.png', 'image');" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to link research.source_record_attachment"
  exit 1
}

# Capture the pre-backup hash/hex so STEP 7 can assert byte-identical restore.
ATTACH_HASH_BEFORE=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT content_hash FROM research.attachment_content WHERE attachment_content_id = $ATTACH_CONTENT_ID;" 2>/dev/null | tr -d ' ')

ATTACH_HEX_BEFORE=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT encode(content, 'hex') FROM research.attachment_content WHERE attachment_content_id = $ATTACH_CONTENT_ID;" 2>/dev/null | tr -d ' ')

if [[ -z "$ATTACH_HASH_BEFORE" || -z "$ATTACH_HEX_BEFORE" ]]; then
  echo "✗ FAIL: failed to read back seeded attachment content"
  exit 1
fi

echo "✓ PASS: Evidence Inbox attachment seed = source_record $ATTACH_RECORD_ID / attachment_content $ATTACH_CONTENT_ID (hash $ATTACH_HASH_BEFORE)"

# ============================================================================
# STEP 4: Create a backup
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 4: Create backup (scripts/backup.sh)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if bash "$SCRIPT_DIR/backup.sh" > /dev/null 2>&1; then
  # Find the most recent dump file
  DUMP_FILE=$(ls -t "$WORKSPACE_DIR/backups"/genealogy_*.dump 2>/dev/null | head -1)
  if [[ -z "$DUMP_FILE" ]]; then
    echo "✗ FAIL: no dump file created"
    exit 1
  fi
  echo "✓ PASS: Backup file = $DUMP_FILE"
else
  echo "✗ FAIL: scripts/backup.sh failed"
  exit 1
fi

# ============================================================================
# STEP 5: Tamper with the database (delete seed record)
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 5: Tamper: DELETE smoke_seed record"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "DELETE FROM genealogy.smoke_seed WHERE id = 1;" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to delete seed record"
  exit 1
}

# Verify it's gone
RECORD_COUNT=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT COUNT(*) FROM genealogy.smoke_seed WHERE id = 1;" 2>/dev/null | tr -d ' ')

if [[ "$RECORD_COUNT" != "0" ]]; then
  echo "✗ FAIL: tamper verification failed: record still exists"
  exit 1
fi

# Also tamper with the Evidence Inbox attachment seed: delete the
# source_record (cascades away its research.source_record_attachment link)
# and separately delete the (undeduped, not-cascaded-to) attachment_content
# row, so STEP 7 proves restore actually brings both back rather than merely
# observing rows nothing ever touched.
docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "DELETE FROM research.source_record WHERE source_record_id = '$ATTACH_RECORD_ID';" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to delete research.source_record for attachment tamper"
  exit 1
}

docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "DELETE FROM research.attachment_content WHERE attachment_content_id = $ATTACH_CONTENT_ID;" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to delete research.attachment_content for attachment tamper"
  exit 1
}

ATTACH_RECORD_COUNT_AFTER_TAMPER=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT COUNT(*) FROM research.source_record WHERE source_record_id = '$ATTACH_RECORD_ID';" 2>/dev/null | tr -d ' ')

if [[ "$ATTACH_RECORD_COUNT_AFTER_TAMPER" != "0" ]]; then
  echo "✗ FAIL: attachment tamper verification failed: source_record still exists"
  exit 1
fi

echo "✓ PASS"

# ============================================================================
# STEP 6: Restore from backup
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 6: Restore from backup (scripts/restore.sh)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if bash "$SCRIPT_DIR/restore.sh" "$DUMP_FILE" > /dev/null 2>&1; then
  echo "✓ PASS"
else
  echo "✗ FAIL: scripts/restore.sh failed"
  exit 1
fi

# ============================================================================
# STEP 7: Verify restored record and database integrity
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 7: Verify restore: check seed record, schemas, and migration journal"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# 7a: Verify the seed record was restored
RESTORED_NOTE=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT note FROM genealogy.smoke_seed WHERE id = 1;" 2>/dev/null | tr -d ' ')

if [[ "$RESTORED_NOTE" != "$SEED_NOTE" ]]; then
  echo "✗ FAIL: restored note mismatch: expected '$SEED_NOTE', got '$RESTORED_NOTE'"
  exit 1
fi
echo "  ✓ Seed record restored correctly: note = '$RESTORED_NOTE'"

# 7b: Verify genealogy schema exists
GENEALOGY_SCHEMA=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'genealogy';" 2>/dev/null | tr -d ' ')

if [[ "$GENEALOGY_SCHEMA" != "1" ]]; then
  echo "✗ FAIL: genealogy schema not found"
  exit 1
fi
echo "  ✓ genealogy schema exists"

# 7c: Verify research schema exists
RESEARCH_SCHEMA=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'research';" 2>/dev/null | tr -d ' ')

if [[ "$RESEARCH_SCHEMA" != "1" ]]; then
  echo "✗ FAIL: research schema not found"
  exit 1
fi
echo "  ✓ research schema exists"

# 7d: Verify the Evidence Inbox attachment seed (Phase 6 Task 5 exit
# criterion, plan §10 step 8) was restored: the source_record row exists
# again, and the attachment_content row exists with byte-identical content
# (same hash AND same hex-encoded bytes as before the backup/tamper cycle).
ATTACH_RECORD_COUNT_AFTER_RESTORE=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT COUNT(*) FROM research.source_record WHERE source_record_id = '$ATTACH_RECORD_ID';" 2>/dev/null | tr -d ' ')

if [[ "$ATTACH_RECORD_COUNT_AFTER_RESTORE" != "1" ]]; then
  echo "✗ FAIL: research.source_record was not restored (count=$ATTACH_RECORD_COUNT_AFTER_RESTORE)"
  exit 1
fi
echo "  ✓ research.source_record restored: $ATTACH_RECORD_ID"

ATTACH_HASH_AFTER=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT content_hash FROM research.attachment_content WHERE attachment_content_id = $ATTACH_CONTENT_ID;" 2>/dev/null | tr -d ' ')

ATTACH_HEX_AFTER=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT encode(content, 'hex') FROM research.attachment_content WHERE attachment_content_id = $ATTACH_CONTENT_ID;" 2>/dev/null | tr -d ' ')

if [[ -z "$ATTACH_HASH_AFTER" || "$ATTACH_HASH_AFTER" != "$ATTACH_HASH_BEFORE" ]]; then
  echo "✗ FAIL: research.attachment_content hash mismatch after restore: expected '$ATTACH_HASH_BEFORE', got '$ATTACH_HASH_AFTER'"
  exit 1
fi

if [[ -z "$ATTACH_HEX_AFTER" || "$ATTACH_HEX_AFTER" != "$ATTACH_HEX_BEFORE" ]]; then
  echo "✗ FAIL: research.attachment_content bytes mismatch after restore (not byte-identical)"
  exit 1
fi
echo "  ✓ research.attachment_content restored byte-identical: hash $ATTACH_HASH_AFTER"

# 7e: Verify migration journal has at least 1 row
MIGRATION_COUNT=$(docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t -c \
  "SELECT COUNT(*) FROM public.schema_version;" 2>/dev/null | tr -d ' ')

if [[ -z "$MIGRATION_COUNT" || "$MIGRATION_COUNT" == "0" ]]; then
  echo "✗ FAIL: migration journal has no entries"
  exit 1
fi
echo "  ✓ Migration journal: $MIGRATION_COUNT migration(s) applied"

echo "✓ PASS"

# ============================================================================
# STEP 8: Cleanup
# ============================================================================
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "STEP 8: Cleanup: DROP smoke_seed table and remove dump file"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "DROP TABLE IF EXISTS genealogy.smoke_seed;" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to drop smoke_seed table"
  exit 1
}

# Clean up the Evidence Inbox attachment seed: deleting the source_record
# cascades away its restored source_record_attachment link; attachment_content
# and the smoke tree are not cascaded, so they're removed explicitly.
docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$CONTAINER_NAME" \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
  "DELETE FROM research.source_record WHERE source_record_id = '$ATTACH_RECORD_ID';
   DELETE FROM research.attachment_content WHERE attachment_content_id = $ATTACH_CONTENT_ID;
   DELETE FROM genealogy.tree WHERE tree_id = '$ATTACH_TREE_ID';" \
  > /dev/null 2>&1 || {
  echo "✗ FAIL: failed to clean up Evidence Inbox attachment seed"
  exit 1
}

if [[ -f "$DUMP_FILE" ]]; then
  rm "$DUMP_FILE" || {
    echo "✗ FAIL: failed to remove dump file"
    exit 1
  }
fi

echo "✓ PASS"

# ============================================================================
# SUCCESS
# ============================================================================
echo ""
echo "╔════════════════════════════════════════════════════════════════╗"
echo "║ ALL CHECKS PASSED — Phase 1 smoke test successful             ║"
echo "╚════════════════════════════════════════════════════════════════╝"
echo ""
