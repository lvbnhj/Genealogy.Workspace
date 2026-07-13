# PostgreSQL Genealogy Workspace — Migration Plan

**Post-Phase-7 addendum (2026-07-13):** the product was extracted from the
`DNA-DB` monorepo into this standalone repository (own `git init`, clean
history — the prior phase-by-phase commit trail lives only in `DNA-DB`'s now-
retired root history). `Genealogy.Workspace.slnf` (a filter into the
monorepo's `DNA-DB.sln`, mentioned throughout the Phase 7 section below) was
replaced by a real standalone `Genealogy.Workspace.sln`; `LICENSE` and
`THIRD-PARTY-NOTICES.md` now live at this repo's root; `scripts/install.sh` /
`install.ps1` were added for a one-command fresh-clone setup. Everything below
this line is the historical record of Phases 0–7 as executed inside the
monorepo — read `.slnf`/"repository-root" mentions in that context.

**Status (2026-07-13): COMPLETE — Phases 0–7 all implemented and on main.** The standalone PostgreSQL genealogy workspace is a full product: PostgreSQL 17 compose + DbUp migrator (9 migrations), production `genealogy` schema, database-neutral GEDCOM parser, staging → preview → duplicate detection → readiness → transactional guarded apply, a 35-tool stdio MCP server (tree query + GEDCOM import + Evidence Inbox), and the Evidence Inbox with binary attachments. 168 .NET integration tests + 13 Python tests pass; backup/restore preserves attachment bytes; a one-command `quickstart.sh` imports a sample GEDCOM and stores an evidence screenshot end-to-end; MIT licensed; no dependency on the SQL Server / DNA code. All blocking decisions (§15) resolved: (1) same repository, prefix `Genealogy.Workspace`; (2) PostgreSQL 17; (3) canonical parent edge = `parent_child` (spouses derived from `family`); (4) attachment limits 20MB/100MB/8MB configurable; (5) tree tools shipped in Phase 5. Per-phase detail below.

Phase 0 complete (docs/PHASE0_BASELINE.md, tag genealogy-baseline-v1). Phase 1 implemented and merged: PostgreSQL 17 compose, Genealogy.Workspace.Data + Migrator (DbUp), migration 0001, integration tests, backup/restore + smoke script.

Phase 2 implemented and merged to main (commits 6a18550 + merge 9743296): migration `0002_genealogy_core_schema.sql` (10 `genealogy.*` core tables — tree, person, person_name, place, family, family_child, parent_child, event, event_citation, name_variant_rule — with same-tree composite FKs, single-default partial index, self-parent/self-spouse checks, no vector/DNA columns); seed migration `0003_seed_name_variant_rules.sql` (intentional no-op — live source table was empty at export); data-access layer `TreeRepository` / `PersonRepository` / `FamilyContextRepository` (parameterized SQL, spouses derived from `family`); 37 integration tests (all passing) covering cross-tree/self-link rejection, no-vector/no-embedding schema shape, Cyrillic+Latin search, family context with remarriage, and index-usage plans. Build clean (0 warnings). Decision 3 (canonical relationship) resolved: `parent_child` from `TreeParentOf` only, spouses derived from `family`+MARR. Decision 4 (attachment size limits) deferred to Phase 6. Decision 5 (which tree tools) covered for Phase 2 by the three repositories; MCP surface is Phase 5.

**Phase 2 stricter-than-source constraints (validate before Phase 3 re-import of real trees):** three uniqueness constraints and one CHECK are intentionally stricter than the SQL Server source and could reject existing Rudenko rows on re-import — (1) `uq_person_name_one_primary` (one `is_primary` name per person); (2) `uq_family_spouse_pair_year` (one family per unordered spouse pair + `coalesce(marriage_year,-1)` — two same-couple families both with NULL year collide); (3) `uq_person_name_normalized` (`person_id, script_code, full_name_normalized`); (4) `person.sex CHECK IN ('M','F')` rejects any other/`'U'` value present in source. Phase 3 must validate real data against these or relax the constraint before apply.

Phase 3 implemented and merged to main (commit a963b58 + merge a3b147e): GEDCOM staging & parser port, scope = stage / preview / pending-list / cancel (duplicate detection, the readiness report, and guarded apply deferred to Phase 4; MCP tool wrappers deferred to Phase 5 with the rest of the server). Deliverables: (a) refactored **copy** of the Python parser at `tools/gedcom/gedcom_tool.py` — stripped of all T-SQL emission (`insert_batch.sql`, `sql_string`) and the hard-coded `DEFAULT_TREE_ID`; `--tree-id` now required, legacy UUID mode explicit-only; UUIDv5 determinism preserved; 13 unittest tests moved to `tools/gedcom/tests/`. The legacy `Python/gedcom_tool.py` is left untouched for the existing SQL Server pipeline (short-term duplication, per §3 vs §10 tension). (b) migration `0004_gedcom_staging_schema.sql` — 12 `genealogy.gedcom_import_*` staging tables (batch-cascade FKs, composite FKs for parsed-name→name and citation/warning→event, status/parser_status/warning_kind CHECKs); staging `sex` deliberately **un**-CHECKed (raw-import fidelity — non-M/F surfaces in preview, not a load failure). (c) migration `0005_gedcom_import_preview.sql` — two STABLE functions faithfully porting `ged.GetGedcomImportPreview`'s change classifier (ADD/UPDATE/MISSING_FROM_IMPORT/REPLACE per entity, MARR/CHR/DEAT special-casing with source-line citations). (d) .NET data layer: `GedcomStagingLoader` (Npgsql binary COPY, one transaction, rollback-on-error), `GedcomStagingService` (stage → run parser + load; list-pending; cancel with APPLIED guard, idempotent, never deletes rows), `GedcomImportPreviewService` (STAGED→PREVIEWED flip + the 0005 functions). 48 integration tests + 13 Python tests, all passing; build clean.

**Phase 3 notes for Phase 4:** (1) SpouseOf preview ADD is derived from `genealogy.family` unordered spouse pairs (the source's directional `ged.TreeSpouseOf` table does not exist in the ported schema), dropping RelationType/FamilyId per-edge equality — see 0005 header. (2) The parser is **lenient**: malformed/garbage GEDCOM input yields a `STAGED` batch with all-zero counts rather than an error (line regex skips non-matching lines; decoding uses `errors="replace"`). Phase 4's readiness/apply gating must treat a zero-count or otherwise-degenerate batch as non-applyable rather than assuming staging rejected bad input.

Phase 4 implemented and merged to main (commit 84c4b8f + merge ab45286): duplicate detection + guarded apply. **User decisions for this phase:** NO in-DB readiness/confirmation gate (apply is faithful to source — applyable from STAGED/PREVIEWED/WAITING_FOR_CONFIRMATION, duplicates advisory-only, no token); legacy cruft removed (the three MARR/CHR/DEAT reconciliation UPDATEs, `TreeRelationship`, `TreeSpouseOf` — none needed in the fresh Phase-2 schema); candidate detail **expanded** beyond the source to include parents/spouses/children per side; the source's inert gaps (`scope_invalid_count` always NULL; `stale`/`accepted` statuses never set; no "materially-changed" re-eval) ported **as-is** and documented. Deliverables: (a) migration `0006_gedcom_duplicate_candidates.sql` — `gedcom_import_duplicate_candidate` table + `generate_…` fn (composite `score = clamp(0.75·name + 0.15·date + 0.10·place − negative)`, additive negative incl. sourced same-event date conflict `+1.0`, disconnected filter, delete-suggested/stale + `IS NOT DISTINCT FROM` rejected-suppression). (b) migration `0007_gedcom_import_readiness.sql` — advisory `gedcom_import_readiness_gates(batch)` (high-confidence-dup blocker ≥0.90, name/date/scope warnings). (c) migration `0008_gedcom_apply.sql` — plpgsql `apply_gedcom_import(batch, delete_missing)`: status guard only, idempotent upserts (person/family via `ON CONFLICT … WHERE IS DISTINCT FROM`; event via stable `(tree_id, person_id, external_event_key)` UPDATE-then-INSERT; citation delete/reinsert GEDCOM-only), `delete_missing` default-off with root safety valve, atomic status→APPLIED. (d) .NET services: `GedcomDuplicateService` (generate/list/expanded-detail/reject), `GedcomReadinessService` (advisory report + WAITING_FOR_CONFIRMATION transition), `GedcomApplyService` (dry-run→preview by default; real apply only on explicit `dryRun:false`). 72 integration tests + 13 Python tests, all passing; build clean. Exit criteria met: double/re-apply idempotency (deterministic UUIDs → no dup rows), atomic rollback (forced mid-apply failure leaves production untouched), duplicate score-bands + negative-evidence via synthetic `.ged` fixtures, `delete_missing` end-to-end. (Exit criterion "blocked batch cannot be applied via lower-level SQL" was intentionally dropped with the no-gate decision.)

**Phase 4 also repaired a Phase 3 regression:** the root `.gitignore` blanket `*.ged` had a negation only for `Python/tests/fixtures/`, so Phase 3's moved fixtures under `Genealogy.Workspace/tools/gedcom/tests/fixtures/` were silently never committed (the GEDCOM tests were green only on the authoring worktree). Added the negation for the new fixtures path and restored `phase0_baseline.ged` / `preflight_names.ged`; the fix lands with Phase 4.

Phase 5 implemented and merged to main (commit d0c4c3f + merge be7eb27): tree query MCP parity — introduces the standalone stdio MCP server `Genealogy.Workspace.McpServer` exposing 21 product-neutral tools (12 tree + 9 GEDCOM) over the Phase 2–4 data layer. **User decisions for this phase:** NO in-DB gate (already decided Phase 4); every tree query is tree-scoped with a **default-tree fallback** and explicit multi-match surfacing (never a silent wrong-tree pick — fixes the source's cross-tree ambiguity bug); the descendants-at-year spouse "bug" is intended (spouse included via marriage regardless of own dates) so only the genuine duplicate-row fan-out was fixed; golden-parity validated **both** ways — a committed synthetic-fixture end-to-end test (always runs) **and** an opt-in Rudenko golden-diff test gated on `GENEALOGY_RUDENKO_GED` (skips on clean checkout, no private data committed). Deliverables: (a) `TreeTraversalRepository` — six recursive/graph queries (ancestors, descendants, descendants-by-year, closest-common-ancestor, person-tree, path-between) as `WITH RECURSIVE` over `parent_child` with `uuid[]` cycle guards the source lacked, tree-scoped, no DNA; also fixes the source's MRCA SUM/MAX path-inflation and `includeRoot=0` bug. (b) flat data layer — `PersonEventsRepository` (life-events), `RichFamilyContextRepository` (5-way parity: events/parents/siblings/marriages/children with birth/death enrichment), `PersonSearchRepository` (find_tree_person), and `TreeResolver`/`PersonResolver` (name-or-GUID + default-tree fallback + multi-match). (c) `Genealogy.Workspace.McpServer` project (ModelContextProtocol 1.1.0, stdio, env-driven config via `WorkspaceDbOptions.FromEnvironment()` — no plaintext appsettings; logging to stderr) with `TreeTools` (12) + `GedcomTools` (9), all returning camelCase JSON with try/catch→`{error}`; added to `DNA-DB.sln` and `.mcp.json`. (d) `docs/MCP.md` + `scripts/publish_mcp.sh` (publishes + generates an env-forwarding `run.sh`). Excluded all DNA: `GetDescendantsWithLinkWithDnaLink`, `linkedDna*` columns, `get_person`/`get_descendants_with_dna`. 148 integration tests + 13 Python tests, all passing; solution builds clean. Exit criteria met: traversal tools agree with an independent oracle (fresh SQL + from-scratch C# BFS) on real staged+applied data, cross-tree isolation (two trees, no leakage), missing-tree/person + duplicate-name + multi-tree + cycle edge cases covered, no tool queries SQL Server or references DNA links. Decision 5 (tree tools for first release) now fully realized.

Phase 6 implemented and merged to main (commit 9b96b9f + merge f6a6b99): Evidence Inbox vertical slice — the plan's first new user-facing feature (greenfield; no SQL Server source). **User decisions for this phase:** attachment size limits (Decision 4) resolved — per-file 20 MB (`GENEALOGY_ATTACHMENT_MAX_BYTES`), per-record 100 MB (`GENEALOGY_ATTACHMENT_MAX_RECORD_BYTES`), base64 MCP payload 8 MB (`GENEALOGY_ATTACHMENT_MAX_BASE64_BYTES`), all configurable; MIME allowlist = PNG/JPEG/GIF/WEBP/TIFF/PDF (server sniffs magic bytes, ignores client-declared type, never executes). Deliverables: (a) migration `0009_research_evidence_schema.sql` — 7 `research.*` tables (source_record, attachment_content, source_record_attachment, source_record_keyword, record_person_mention, record_place_mention, person_link_candidate) with the same-tree composite-FK pattern (accepted links + candidates can only target a `genealogy.person` in the record's tree), SHA-256 content dedup (`attachment_content.content_hash` unique, global/content-addressed), and the content-preserving cascade asymmetry (deleting a record cascades to attachment LINKS but never to the shared content bytes). (b) attachment pipeline — `AttachmentRepository` + `MimeSniffer` + `AttachmentOptions`: read bytes (base64 or local path), server-computed SHA-256/MIME/length, size + allowlist validation, dedup-by-hash, byte-identical retrieval, link-only delete, and an explicit `CleanupOrphanedContentAsync` (never a cascade side effect). (c) record/mention/link services — record CRUD, keywords, person/place mentions, `search_research_records` (full structured filter set), an explainable `suggest_record_person_links` scorer that never modifies the tree, and accept/reject link lifecycle (accept sets the mention's `accepted_person_id` + supersedes siblings; rejected candidates survive re-suggestion). (d) 14 evidence MCP tools (`ResearchTools` + `ResearchAttachmentTools`) registered on the server. Constraints honored: `tree_id` mandatory; no auto-creation of `genealogy.person`; mentions are evidence, not tree persons; DB bytes are the durable copy (URL/path are provenance/import-only). `docs/MCP.md` documents the tools + attachment rules; `scripts/smoke.sh` extended to seed an attachment and prove it survives backup→tamper→restore byte-identical (ran live, passed). 167 integration tests + 13 Python tests, all passing; solution builds clean. Exit criteria met — the full 8-step primary scenario (create tree → inbox record → byte-identical screenshot round-trip → Семашко mention + place/year → structured search → suggest without tree mutation → accept/reject → backup/restore preserves record + bytes). backup.sh/restore.sh unchanged (already capture research + bytea via `pg_dump -Fc`).

Phase 7 implemented on branch worktree-genealogy-phase7 (not yet committed): packaging / release candidate — the FINAL phase. **User decision:** MIT license (unrestricted use). Deliverables: (a) `LICENSE` (MIT, repo root) + `Genealogy.Workspace/THIRD-PARTY-NOTICES.md` (Npgsql=PostgreSQL License, DbUp/ModelContextProtocol/Microsoft.Extensions=MIT — all permissive, MIT-compatible). (b) `Genealogy.Workspace.slnf` — a workspace-only solution filter (the 4 workspace projects; no DNA/SQL Server project), and `scripts/smoke.sh` repointed to build it instead of `DNA-DB.sln`, so a release never requires the DNA code to compile (grep confirms zero DNA `ProjectReference`s). (c) a `quickstart` command on the Migrator + `scripts/quickstart.sh` — the runnable clean-machine proof: up → migrate → create tree → stage+apply the sample GEDCOM → store an evidence screenshot → print a summary (verified end-to-end: 28 persons imported, attachment stored byte-identical, idempotent on re-run). (d) `UpgradePreservesContentTests` — migrates to a 0001–0008 subset, seeds content, upgrades to HEAD (only 0009 applies), and asserts prior content is preserved and `research` is now usable. (e) README expanded with Quickstart (import + evidence), MCP-server publish/registration, workspace-only build, and license sections. 168 integration tests + 13 Python tests, all passing; `Genealogy.Workspace.slnf` builds clean. **All four Phase 7 exit criteria met:** a clean machine installs + imports a GEDCOM + adds an evidence screenshot from documented commands (`quickstart.sh`, run live); upgrade from the prior migration set preserves content (test); failure/recovery is documented + smoke-tested (backup→tamper→restore, incl. attachment bytes); no dependency on the SQL Server database, DNA projects, or DNA data (`.slnf` + grep). **The PostgreSQL genealogy workspace migration (Phases 0–7) is complete.**

## 1. Objective

Create a separate, locally deployable genealogy workspace backed by PostgreSQL.
The first product scope contains the genealogy tree, safe GEDCOM import/preflight,
genealogy MCP tools, and Evidence Inbox with binary attachments stored in the
database.

This plan does not decide the future of the DNA part of DNA-DB. It neither removes
genealogy code from DNA-DB nor builds synchronization or integration between the
two products. The DNA domain is treated as deferred debt that may migrate later;
the only obligation this plan takes on for it is not to burn bridges — stable
person UUIDs are preserved (section 11) and the tree↔DNA link mapping is
snapshotted in Phase 0.

## 2. Product boundary

### In scope

- multiple genealogy trees;
- persons, names, families, parent/child and spouse relationships;
- events, places, event citations;
- GEDCOM export/staging/preview/readiness/apply/cancel workflow;
- name parsing and normalization rules;
- GEDCOM duplicate candidates;
- tree search and traversal through MCP;
- Evidence Inbox belonging to a specific tree;
- record text, archive citation, keywords, person/place mentions;
- screenshots and documents persisted as binary content in PostgreSQL;
- suggested/reviewed/accepted/rejected links from evidence mentions to tree persons;
- local Docker-based installation, migrations, backup and restore;
- deterministic and integration tests.

### Explicitly out of scope

- DNA persons, kits and matches;
- DNA/tree links;
- shared cM, segments, bins, clusters and family groups;
- phasing, MRCA DNA scoring and chromosome painting;
- synchronization with the existing SQL Server database;
- deleting or deprecating genealogy code in DNA-DB;
- Web UI;
- OCR/HTR;
- embeddings and semantic search;
- automatic person creation, automatic evidence linking or automatic merge;
- production tree merge workflow, timelines, conflict detection and hypotheses.

## 3. Repository strategy

**Decision (2026-07-12): same repository.** The PostgreSQL workspace lives in the
DNA-DB repository as separate projects — no new repository. Isolation is enforced
at the project/solution level instead of the repository level: the new projects
must not reference SQL Server projects, `Microsoft.Data.SqlClient`, or any DNA
code, and nothing outside the workspace may reference the new projects.

Working name for the project prefix: `Genealogy.Workspace`.

Proposed layout inside DNA-DB:

```text
DNA-DB/
  Genealogy.Workspace/
    src/
      Genealogy.Workspace.McpServer/
      Genealogy.Workspace.Data/
    database/
      migrations/
      queries/
      functions/
      seeds/
    tests/
      Genealogy.Workspace.IntegrationTests/
      fixtures/
    docs/
    docker-compose.yml
    .env.example
    README.md
```

The GEDCOM tooling under `Python/` and its tests are shared with the existing
import pipeline and stay where they are; the workspace consumes their
database-neutral artifacts only.

Architectural rules:

- PostgreSQL is the only supported database in the new product.
- Do not introduce a SQL Server/PostgreSQL compatibility layer.
- Use `Npgsql` in .NET; do not wrap `SqlConnection` and `NpgsqlConnection` behind
  a fake lowest-common-denominator provider.
- Keep schema changes in ordered, immutable SQL migrations.
- Keep complex read queries in versioned SQL files or PostgreSQL functions when
  this materially improves query planning/reuse.
- Keep workflow orchestration and response-contract composition in the .NET
  service layer rather than moving all new behavior into database procedures.
- Preserve raw GEDCOM/evidence values alongside parsed/normalized values.
- Every tree-owned row must be protected against cross-tree references.

## 4. Target schemas

Use two PostgreSQL schemas:

- `genealogy` — production tree and GEDCOM import data;
- `research` — Evidence Inbox, attachments, mentions and reviewed links.

Avoid retaining the name `ged` for the whole production domain: GEDCOM is an
import format, while manually researched tree data is not necessarily GEDCOM.

Use `snake_case` for new PostgreSQL identifiers. MCP JSON contracts may retain
stable user-facing names independent of SQL naming.

## 5. Source inventory and disposition

### Production genealogy tables

| SQL Server source | PostgreSQL target | Decision |
|---|---|---|
| `ged.TreeDataset` | `genealogy.tree` | Port and remove hard-coded default UUID assumptions |
| `ged.TreePerson` | `genealogy.person` | Port without DNA/vector columns |
| `ged.TreePersonNames` | `genealogy.person_name` | Port; canonical production name table |
| `ged.TreeNameVariant` | — | Do not port separately; reconcile into `person_name` before migration |
| `ged.TreePlace` | `genealogy.place` | Port without embeddings; make ownership semantics explicit |
| `ged.TreeEvent` | `genealogy.event` | Port |
| `ged.TreeEventCitation` | `genealogy.event_citation` | Port |
| `ged.TreeFamily` | `genealogy.family` | Port without SQL Server computed columns |
| `ged.TreeFamilyChild` | `genealogy.family_child` | Port |
| `ged.TreeParentOf` | `genealogy.parent_child` | Use as the single canonical parent edge model |
| `ged.TreeRelationship` | — | Do not port as a duplicate parent edge table |
| `ged.TreeSpouseOf` | — | Derive spouse relationships from `family`; do not store duplicate directed edges |
| `ged.TreePersonLink` | — | DNA-related, excluded |
| `ged.TreePersonG` | — | Legacy/special-purpose, excluded unless a concrete genealogy consumer is found |
| `ged.NameVariantRule` | `genealogy.name_variant_rule` | Port |

Before implementing the target schema, audit current consumers of
`TreeParentOf` versus `TreeRelationship`. Select one canonical meaning and write
compatibility tests from real tree examples. Do not silently union both sources.
Note the tables also differ structurally: `TreeParentOf` keys on
`(Parent, Child, RelationType)` while `TreeRelationship` keys on
`(Parent, Child)` — reconcile the semantics, not just the row sets.

Also audit `TreeSpouseOf` attributes before dropping it: it carries
`RelationType`, `MarriageYear` and `MarriagePlaceId`. Verify every such value is
represented by a family `MARR` event (or migrate it to one) so that deriving
spouse relationships from `family` loses no data.

### GEDCOM staging tables

Port the complete staging lifecycle:

- import batch;
- staged persons and names;
- parsed names;
- places;
- families and family children;
- parent edges;
- events and citations;
- date warnings;
- duplicate candidates.

Do not port `GedcomImportSpouseOf` unless the importer still has a consumer that
cannot be represented through staged families. Prefer one representation of a
relationship in staging and production.

### Procedures and tools

Port in behavioral groups, not file-by-file:

1. tree creation and lookup;
2. person search and family context;
3. ancestor/descendant/path traversal;
4. GEDCOM stage and preview;
5. readiness gates and lifecycle transitions;
6. duplicate candidate generation and review;
7. apply transaction.

Exclude `GetDescendantsWithDnaLink` and all result columns derived from
`ged.TreePersonLink` or `dbo.Person`.

## 6. PostgreSQL type and behavior mapping

| SQL Server | PostgreSQL | Notes |
|---|---|---|
| `uniqueidentifier` | `uuid` | Generate with PostgreSQL UUID function or application-provided UUID |
| `nvarchar(n/max)` | `text` or `varchar(n)` | Prefer `text`; limits only for actual domain constraints |
| `datetime2` | `timestamptz` | Only for audit/system instants (created/updated). Historical genealogical dates are never timestamps: keep raw date text plus parsed year/range columns |
| `bit` | `boolean` | |
| `varbinary(max)` | `bytea` | Evidence attachment content |
| `IDENTITY` | `generated ... as identity` | |
| filtered index | partial index | |
| persisted computed column | generated column or expression index | Prefer expression/unique index when sufficient |
| recursive CTE | `WITH RECURSIVE` | Re-test cycle handling and depth limits |
| `THROW` | service exception or PL/pgSQL exception | Stable MCP error codes are defined above SQL |
| `MERGE` | `INSERT ... ON CONFLICT`, explicit update/delete | Do not translate mechanically |

All confidence values retain `[0.0, 1.0]` checks. All lifecycle/status values have
database constraints and corresponding application enums/constants.

## 7. Target integrity rules

The new schema must correct integrity weaknesses rather than reproduce them:

- `person(tree_id, external_id)` is unique when `external_id` is present;
- only one default tree is allowed;
- a tree root must belong to the same tree;
- parent and child must belong to the edge's tree;
- a person cannot be their own parent or spouse;
- a family cannot contain the same spouse twice;
- family members and children must belong to the family's tree;
- event person, related person, family and place references must be tree-compatible;
- accepted evidence links must point to a person in the evidence record's tree;
- raw source text and original attachment content are immutable;
- import apply is transactional and idempotent for the same staged identifiers;
- rejected candidates survive regeneration unless input evidence materially changes.

Where ordinary foreign keys cannot enforce same-tree ownership, use composite
unique keys plus composite foreign keys. Use triggers only when declarative
constraints cannot express the rule.

## 8. Evidence Inbox MVP schema

### `research.source_record`

Required core fields:

- `source_record_id uuid`;
- `tree_id uuid not null`;
- `title text not null`;
- `record_type text not null`;
- `record_text text null`;
- `transcription text null`;
- `record_date_text text null`;
- parsed date range fields, nullable;
- place/church freetext, nullable;
- archive, fond, opis, sprava, page/arkush fields, nullable;
- `citation_text text null`;
- `source_url text null` as provenance only;
- `status` constrained to `inbox`, `in_review`, `resolved`, `dismissed`, `archived`;
- created/updated metadata.

`tree_id` is mandatory. A person/event/family link is not mandatory.

### Attachments

`research.attachment_content`:

- SHA-256 hash with unique constraint;
- original content as `bytea`;
- verified MIME type;
- byte length;
- creation timestamp.

`research.source_record_attachment`:

- link to source record and content;
- original filename, caption and sequence;
- attachment type;
- optional provenance URL.

Rules:

- database bytes are the durable copy;
- a local path is accepted only as an import input and is never durable storage;
- the server computes hash, MIME type and length;
- enforce configurable per-file and per-record limits;
- deduplicate content by hash;
- never execute uploaded content;
- deleting a record removes attachment links; unreferenced content is removed by a
  controlled cleanup operation, not an unsafe immediate cascade.

### Search and mentions

Create:

- `research.source_record_keyword`;
- `research.record_person_mention`;
- `research.record_place_mention`;
- `research.person_link_candidate`.

Accepted/rejected tree linking applies to a person mention, not directly to the
whole record. One record can mention multiple people and produce multiple links.

MVP search filters:

- tree;
- status and record type;
- title/body/transcription text;
- surname/given name;
- place;
- archive citation components;
- year/date range;
- keyword;
- linked/unlinked state.

Use PostgreSQL full-text search and trigram indexes only after baseline structured
queries exist. Embeddings are outside this migration.

## 9. MCP contract

The first release exposes genealogy-only tools under a product-neutral server
name. Do not expose DNA terminology in tool descriptions or configuration.

### Tree tools

- `create_tree_dataset`;
- `list_tree_datasets`;
- `find_tree_person`;
- `get_tree_person`;
- `get_person_family_context`;
- `get_person_life_events`;
- `get_ancestors`;
- `get_descendants`;
- `get_path_between_persons`.

### GEDCOM tools

- `stage_gedcom_import`;
- `get_gedcom_import_preview`;
- `get_gedcom_import_readiness_report`;
- `list_pending_gedcom_imports`;
- `get_gedcom_import_duplicate_candidates`;
- `get_gedcom_import_duplicate_candidate_detail`;
- `reject_gedcom_import_duplicate_candidate`;
- `apply_gedcom_import`;
- `cancel_gedcom_import`.

### Evidence tools

- `add_research_record`;
- `get_research_record`;
- `update_research_record`;
- `search_research_records`;
- `add_research_attachment`;
- `get_research_attachment`;
- `list_research_attachments`;
- `delete_research_attachment`;
- `add_record_person_mention`;
- `add_record_place_mention`;
- `suggest_record_person_links`;
- `accept_record_person_link`;
- `reject_record_person_link`.

For MVP, `add_research_attachment` supports a bounded base64 payload and import
from a local path visible to the MCP server. Add chunked upload only when real
attachment sizes exceed the chosen safe request limit.

Every mutation returns stable JSON containing IDs, status and a short audit
summary. Readiness/apply failures return structured `blocker`, `warning` and
`info` gates rather than relying on exception text parsing.

## 10. Execution phases

### Phase 0 — Freeze and characterize the source

Deliverables:

- tag or commit a reproducible genealogy baseline in DNA-DB;
- build the current solution and DACPAC;
- run current Python GEDCOM tests;
- export schema inventory and procedure/tool inventory;
- identify consumers of both parent-edge representations;
- audit `TreeSpouseOf` attributes against family `MARR` events;
- export a `TreePersonLink` snapshot (`TreePersonId` ↔ `PersonId`) as baseline
  insurance for the deferred DNA migration — it is not ported, only preserved;
- create a sanitized GEDCOM regression fixture;
- capture expected counts and golden query results from the Rudenko tree.

Exit criteria:

- baseline can be rebuilt from a clean checkout;
- fixtures contain no private material that cannot enter the new repository;
- expected persons, names, families, events, citations, places and edge counts are recorded;
- at least ten representative genealogy queries have golden results.

### Phase 1 — Bootstrap the new workspace

Deliverables:

- `Genealogy.Workspace/` project tree and projects added to the solution;
- PostgreSQL Docker Compose configuration with persistent volume and healthcheck;
- Npgsql connectivity and configuration validation;
- numbered SQL migration runner;
- integration-test database lifecycle;
- local backup/restore scripts and documentation;
- CI build plus migration-from-empty test.

Exit criteria:

- one command starts PostgreSQL and applies migrations;
- tests create an isolated database and tear it down;
- backup and restore reproduce a seed record;
- no SQL Server package or configuration remains.

### Phase 2 — Production genealogy schema

Deliverables:

- `genealogy` schema and core tables;
- same-tree constraints and indexes;
- canonical relationship model;
- seed/migration for name variant rules;
- tree create/list, person get/search and basic family-context queries.

Exit criteria:

- schema rejects cross-tree relationships and self-links;
- query plans use expected indexes for tree/name lookups;
- core query integration tests pass with Cyrillic and Latin names;
- no vector/embedding or DNA columns exist.

### Phase 3 — GEDCOM staging and parser port

Deliverables:

- port `gedcom_tool.py` (and the parts of `ged2csv.py` still needed) with its
  tests into `tools/gedcom`; this is a refactor, not a copy — strip T-SQL
  artifact generation (`insert_batch.sql` etc.) and the hard-coded default tree
  UUID so the tool emits only database-neutral artifacts (TSV/JSON);
- PostgreSQL staging tables;
- bulk loading through PostgreSQL COPY or Npgsql binary import;
- import batch lifecycle;
- date warnings and parsed name rows;
- stage, preview, pending-list and cancel MCP tools.

The Python parser remains database-agnostic: it produces deterministic artifacts.
Database loading belongs to the .NET/data layer, not to Python shell calls with
database credentials.

Exit criteria:

- the same GEDCOM fixture produces stable staging rows across repeated runs;
- staging never changes production tree rows;
- cancel prevents apply;
- preview counts match independently queried staging/production differences;
- malformed input fails without leaving an apparently applyable batch.

### Phase 4 — Duplicate detection and guarded apply

Deliverables:

- port duplicate scoring behavior with PostgreSQL queries;
- candidate detail and reject/stale behavior;
- explicit readiness policy;
- transactional and idempotent import apply;
- lifecycle enforcement in the database transaction, not only MCP instructions.

Initial policy:

- high-confidence unresolved duplicate: blocker;
- ambiguous/low-confidence name: warning;
- approximate/range/open/unparsed date: warning;
- scope violation: blocker;
- apply requires `waiting_for_confirmation` plus an explicit confirmation token or
  expected readiness version;
- apply does not auto-merge and does not delete missing people by default.

Exit criteria:

- blocked batch cannot be applied by calling lower-level SQL directly;
- concurrent/double apply cannot create duplicate rows;
- transaction rollback leaves no partial production changes;
- duplicate fixtures reproduce agreed score bands and negative evidence behavior.

### Phase 5 — Tree query MCP parity

Deliverables:

- port selected tree tools;
- remove DNA-derived columns from contracts;
- recursive traversal with depth limits and cycle protection;
- stable JSON error/result contracts;
- tool documentation.

Exit criteria:

- golden source queries and PostgreSQL MCP results agree semantically;
- tests cover missing tree/person, duplicate names, multiple trees and cycles;
- no tool queries SQL Server or refers to DNA links.

### Phase 6 — Evidence Inbox vertical slice

Deliverables:

- research schema and migrations;
- add/get/update/search record tools;
- binary attachment add/get/list/delete tools;
- keywords and manual person/place mentions;
- manual candidate creation and accept/reject person links;
- attachment size/security validation;
- backup/restore verification with real binary content.

Exit criteria — primary user scenario:

1. Create or select the Rudenko tree.
2. Add an inbox record with title, freetext and archive citation.
3. Store a screenshot in PostgreSQL and retrieve byte-identical content.
4. Add a `Семашко` person mention and place/year context.
5. Find the record by surname, place, year and freetext.
6. Suggest a tree person without modifying the tree.
7. Accept or reject the candidate explicitly.
8. Backup and restore the database without losing the record or screenshot.

### Phase 7 — Packaging and release candidate

This phase packages the standalone genealogy workspace only; it does not address
DNA-DB integration or retirement.

Deliverables:

- local installation guide;
- Docker Compose release profile;
- migration/upgrade command;
- backup/restore and data-directory documentation;
- configuration for MCP clients;
- sample non-private tree/evidence fixture;
- release smoke-test script;
- license and third-party notices.

Exit criteria:

- a clean machine can install, import a GEDCOM and add an evidence screenshot from
  documented commands;
- upgrade from the previous migration set preserves content;
- failure/recovery instructions are tested;
- no dependency on the SQL Server database, DNA projects or DNA data exists.

## 11. Data migration and validation strategy

The primary migration path for existing genealogy trees is re-import from the
original GEDCOM through the new guarded pipeline. This validates the product's
normal user path and avoids copying SQL Server-specific internal artifacts.

Use direct SQL Server-to-PostgreSQL transfer only for genealogy data not preserved
in GEDCOM, such as reviewed normalization state or manual citations. Such transfer
must be a separate one-time utility with explicit field mapping and reconciliation
report.

Validation report per tree:

- person, name and surname counts;
- family, parent edge and family-child counts;
- event counts by type;
- citation and place counts;
- root person identity;
- orphan and cross-tree reference counts;
- duplicate external IDs;
- representative ancestor/descendant paths;
- representative person family contexts;
- raw GEDCOM date/name preservation.

Do not require identity/sequence numbers to match.

**Preserving stable person/tree UUIDs across re-import is a requirement, not an
option.** The deferred DNA migration depends on it: `ged.TreePersonLink` in
DNA-DB references tree persons by `TreePersonId`, and if re-import assigns new
UUIDs that bridge is destroyed, turning a future DNA migration into manual
person re-matching. `gedcom_tool.py` already derives deterministic UUIDv5
values from a fixed namespace, so this is cheap — verify determinism with a
repeated-import test and add UUID equality to the validation report. Fall back
to external IDs and source keys only for entities that never had stable UUIDs.

## 12. Test strategy

Required layers:

- Python unit tests for deterministic GEDCOM parsing;
- migration tests from an empty database;
- migration upgrade tests from every released schema version;
- PostgreSQL constraint tests;
- query integration tests with real Unicode and ambiguous genealogy cases;
- MCP contract tests;
- attachment round-trip, checksum, duplicate and size-limit tests;
- GEDCOM stage/preview/apply/cancel end-to-end tests;
- concurrency tests for apply and attachment deduplication;
- backup/restore smoke tests.

Minimum fixtures:

- small valid family tree;
- multiple trees with overlapping external IDs;
- duplicate candidates within import and against production;
- disconnected and partially connected people;
- localized exact, approximate, range, open-bound and invalid dates;
- Cyrillic, Polish and Latin name variants;
- event citations;
- malicious/invalid attachment metadata and oversized content;
- cyclic/invalid relationship attempts.

## 13. Operational defaults

- PostgreSQL listens only on local interfaces by default.
- Credentials are generated/configured outside source control.
- Database volume is outside the Git checkout.
- Attachment limit is configurable; choose a conservative MVP default after
  measuring real screenshots.
- Logs never contain attachment bytes, full transcriptions or credentials.
- Healthcheck verifies database connectivity and migration version.
- Automatic destructive cleanup is disabled.
- Backups include both `genealogy` and `research` schemas and therefore all
  attachment bytes.

## 14. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Mechanical T-SQL translation changes behavior | Golden-query tests and behavioral porting by workflow |
| Existing relationship tables disagree | Phase 0 reconciliation before selecting canonical edges |
| GEDCOM re-import loses manual SQL-only data | Inventory and explicit one-time transfer report |
| Attachments make backups large | Hash deduplication, limits, measured restore tests; no premature external storage |
| Recursive queries regress | Representative deep-tree benchmarks and mandatory depth/cycle guards |
| MCP accepts unsafe local paths | Treat path as import-only, validate access policy, persist bytes immediately |
| New repository drifts into DNA concerns | Enforce explicit scope and reject DNA-derived schema/tool dependencies |
| Over-engineering delays the useful product | Deliver Evidence Inbox as the first new vertical slice after tree/import parity |

## 15. Decisions required before implementation starts

Only these decisions block coding:

1. ~~Final repository/product name~~ — resolved 2026-07-12: same repository,
   project prefix `Genealogy.Workspace`.
2. PostgreSQL major version pinned in Docker and CI.
3. Canonical parent/family relationship representation after source reconciliation.
4. Maximum single attachment and total attachment size per record.
5. Which current tree MCP tools are required for the first release beyond the
   minimum list in this plan.

Preserving existing stable tree/person UUIDs across re-import is no longer an
open decision — it is required (see section 11) because of the deferred DNA
migration.

The future location of DNA functionality, synchronization with DNA-DB, Web UI,
embeddings and semantic search are explicitly not blocking decisions.

## 16. Recommended first implementation ticket

Complete Phase 0 only. Do not start translating tables until the current genealogy
baseline, relationship reconciliation and golden query set are committed. The
output of Phase 0 is the evidence needed to make the PostgreSQL schema deliberate
rather than a copy of contradictory SQL Server structures.
