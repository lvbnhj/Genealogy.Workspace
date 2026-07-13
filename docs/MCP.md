# Genealogy.Workspace.McpServer

A .NET 10 stdio [Model Context Protocol](https://modelcontextprotocol.io) server that gives an LLM
direct, read/write access to the **product-neutral genealogy tree workspace** (PostgreSQL, see
`docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md`) — browsing a genealogy tree and staging/applying
GEDCOM imports into it.

**No DNA tools live here.** This server is deliberately scoped to genealogy-tree data only: no DNA
match clustering, phase inference, MRCA scoring, or chromosome painting, and no field in any tool's
JSON response links back to DNA match data. That functionality remains in the separate
`McpServer/DnaAnalysis.McpServer` (SQL Server, see the root `docs/MCP.md`).

Transport is stdio JSON-RPC: JSON-RPC responses go to stdout, all logs go to stderr (required — the
stdio transport would otherwise treat log lines as malformed JSON-RPC messages).

## Component layout

```
Genealogy.Workspace/
└── src/
    ├── Genealogy.Workspace.Data/            — repositories, resolvers, traversal, staging services
    │   ├── Repositories/                    — TreeRepository, PersonRepository, PersonSearchRepository,
    │   │                                       RichFamilyContextRepository, PersonEventsRepository, ...
    │   ├── Resolvers/                        — TreeResolver, PersonResolver (name-or-GUID -> single row,
    │   │                                       or an explicit {error[, candidates]}, never a silent pick)
    │   ├── Traversal/                        — TreeTraversalRepository (ancestors/descendants/common
    │   │                                       ancestor/path, cycle-protected recursive SQL)
    │   ├── Staging/                          — GedcomStagingService / GedcomImportPreviewService /
    │   │                                       GedcomDuplicateService / GedcomReadinessService /
    │   │                                       GedcomApplyService
    │   └── Research/                         — SourceRecordRepository, RecordMentionRepository,
    │                                             PersonLinkService, SourceRecordSearchRepository,
    │                                             AttachmentRepository, AttachmentOptions, MimeSniffer
    │                                             (the Evidence Inbox — migration 0009, `research` schema)
    └── Genealogy.Workspace.McpServer/
        ├── Program.cs                        — host, DI registrations, tool registration
        └── Tools/
            ├── TreeTools.cs                   — 12 tree-browsing tools
            ├── GedcomTools.cs                 — 9 GEDCOM staging/preview/apply tools
            ├── ResearchTools.cs               — 10 Evidence Inbox record/mention/link tools
            └── ResearchAttachmentTools.cs      — 4 Evidence Inbox binary attachment tools
```

## Tool contract conventions

- Every tool returns `Task<string>` — camelCase JSON, serialized via the shared `McpJson.Options`.
- Every tree-scoped tool takes an optional `tree` parameter (name or GUID); it resolves via
  `TreeResolver` first. Omitted `tree` falls back to the one tree flagged `is_default`. An
  unresolved tree (not found, or a name matching more than one tree) returns `{ "error": "..." }`.
- Every person-scoped tool then resolves its `person`/`name1`/`ancestor`/etc. parameter (name or
  GUID) via `PersonResolver` *within that tree*. A unique exact-name match resolves automatically; a
  multi-match returns `{ "error": "...", "candidates": [{ "personId", "fullName" }, ...] }` — never a
  silent pick; a not-found returns `{ "error": "..." }`.
- Recursive traversal (`get_ancestors`, `get_descendants`, `get_person_tree`,
  `get_path_between_persons`, `get_closest_common_ancestor`) carries an explicit visited/path array
  in its SQL and is cycle-protected: a cyclic `parent_child` graph terminates cleanly at the natural
  frontier rather than looping or blowing past its generation/depth cap.
- No tool ever queries SQL Server, and no JSON field links back to a DNA match, cluster, or phase —
  this is the product-neutral genealogy server. See `McpTreeParityTests` /
  `RudenkoGoldenParityTests` (Phase 5 Task 6) for the end-to-end proof of both properties, and
  `TreeToolsTests` for the per-tool `{error,candidates}` / unresolvable-tree / no-DNA-leak coverage.

## Tools (35 total)

### Tree tools (12) — `Tools/TreeTools.cs`

| Tool | Params | Returns |
|---|---|---|
| `list_tree_datasets` | — | Every tree dataset (`treeId`, `name`, `description`, `isDefault`, `rootPersonId`, `createdAt`), ordered by name. |
| `create_tree_dataset` | `name`, `description?`, `isDefault?` | The newly created tree row. Errors on a duplicate name or a second default tree. |
| `get_tree_person` | `person`, `tree?` | One person's core record plus their primary name (given/surname/script/type). |
| `find_tree_person` | `name`, `father?`, `mother?`, `child?`, `spouse?`, `place?`, `yearFrom?`, `yearTo?`, `maxResults?`, `tree?` | Up to `maxResults` substring matches (`count` + `people[]`), each filter narrowing via `EXISTS`. |
| `get_person_family_context` | `person`, `tree?` | Full context: `events[]`, `parents[]`, `siblings[]`, `marriages[]` (spouse + marriage date/place), `children[]`. |
| `get_person_life_events` | `person`, `tree?` | All life events in chronological order, verbatim `dateRaw`/`placeRaw` preserved. |
| `get_ancestors` | `person`, `maxGenerations?` (default 6), `tree?` | Every ancestor up to N generations back, 1-based `generation`, with birth/death year+place. |
| `get_descendants` | `ancestor`, `maxGenerations?` (default 8), `tree?` | Every descendant up to N generations, with `generation` + `parentPersonId`. |
| `get_descendants_by_year` | `ancestor`, `year`, `maxGenerations?`, `tree?` | Descendants alive at `year`, plus their spouses as separate `personType:"SPOUSE"` rows. |
| `get_closest_common_ancestor` | `name1`, `name2`, `name3?`, `maxDepth?` (default 12), `tree?` | The closest shared ancestor (2 or 3 people) and each input's depth to it. |
| `get_person_tree` | `person`, `direction?` (`ancestors`/`descendants`, default `ancestors`), `maxGenerations?` (default 6, capped 50), `includeRoot?`, `tree?` | A full up/down traversal tree with edge metadata (`edgeFromPersonId`/`edgeToPersonId`) and a `path` string per node. |
| `get_path_between_persons` | `person1`, `person2`, `maxDepth?` (default 20), `tree?` | Shortest relationship path (`stepCount` + `steps[]` with `PARENT_OF`/`CHILD_OF` relations); empty `steps` when unconnected. |

### GEDCOM import tools (9) — `Tools/GedcomTools.cs`

Thin wrappers over the Phase 3/4 staging services. **`apply_gedcom_import` enforces no gate**: the
readiness report and duplicate candidates are advisory only, never blocking. `dryRun` defaults to
`true` everywhere it applies, so a real write to the tree always requires an explicit `dryRun:false`.

| Tool | Params | Returns |
|---|---|---|
| `stage_gedcom_import` | `filePath`, `tree?`, `root?`, `notes?`, `legacyIds?`, `batchId?`, `outputDirectory?` | `importBatchId` + staged row counts. No production tree changes. |
| `get_gedcom_import_preview` | `importBatchId` | Read-only ADD/UPDATE/MISSING/REPLACE change summary + a capped sample of person-level changes. |
| `list_pending_gedcom_imports` | `tree?`, `includePreviewed?` (default true), `topN?` (default 50) | Staged/previewed/waiting batches not yet applied or cancelled. |
| `cancel_gedcom_import` | `importBatchId`, `reason?` | Marks the batch `CANCELLED`. Errors if already `APPLIED`. |
| `get_gedcom_import_readiness_report` | `importBatchId`, `minDuplicateScore?` (default 0.75) | Four advisory gates (high-confidence duplicates, name parsing, date warnings, scope-invalid) + `canApplyWithoutReview`/`requiresExplicitConfirmation`. |
| `find_import_duplicate_candidates` | `importBatchId`, `minScore?` (default 0.75), `topN?` (default 100) | Regenerates then lists duplicate candidates (`within_import` / against-existing-tree), read-only. |
| `get_duplicate_candidate_detail` | `duplicateCandidateId` | Full evidence for one candidate: header, matching BIRT/CHR/DEAT/MARR events, parents/spouses/children per side. |
| `reject_tree_person_merge_candidate` | `duplicateCandidateId` | Marks the candidate rejected so regeneration won't re-suggest it. Does not merge/alter tree persons. |
| `apply_gedcom_import` | `importBatchId`, `deleteMissing?` (default false), `dryRun?` (default true) | Applies (or, in dry-run, previews) idempotent upserts to the tree; `changes[]` + a status/note summary. |

### Evidence Inbox tools (14) — `Tools/ResearchTools.cs` + `Tools/ResearchAttachmentTools.cs`

The Evidence Inbox lets an LLM capture archival/vital records (church register entries, archive
files, census pages, ...) it encounters during research, before — or instead of — committing
anything to the tree. It is layered directly over the `research` schema (migration
`0009_research_evidence_schema.sql`) via the Data-layer services in `Genealogy.Workspace.Data/Research/`
(`SourceRecordRepository`, `RecordMentionRepository`, `PersonLinkService`, `SourceRecordSearchRepository`,
`AttachmentRepository`). See `ResearchToolsTests` and `EvidenceInboxExitScenarioTests` (Phase 6 Task 5
exit criterion) for the exact tool-level wiring and an end-to-end walkthrough of the primary user
scenario (docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10).

Three product rules apply across every tool in this group:

- **Tree-scoped, not tree-modifying.** Every source record belongs to exactly one tree (`tree`
  resolves the same way as every `TreeTools` call). No Evidence Inbox tool ever inserts, updates, or
  deletes a `genealogy.person`/`person_name`/`family`/`event`/`parent_child` row — not even
  `accept_record_person_link`, which only writes to the `research` schema (the mention's
  `accepted_person_id`/`status`/`confidence` columns and the candidate's `status`). The only way a
  `genealogy.person` row is created is GEDCOM import (`GedcomTools`), which is out of scope for this
  group.
- **Mentions are evidence, not tree persons.** A `record_person_mention` is just a claim that a
  record names someone — it exists whether or not it is ever linked to anyone in the tree.
  `suggest_record_person_links` scores existing tree persons against a mention and persists
  candidates read-only; `accept_record_person_link` / `reject_record_person_link` decide a
  candidate's fate but never create a new tree person from a mention.
- **No auto person creation, ever.** There is no tool, in this group or any other, that manufactures
  a `genealogy.person` row from a record or a mention. Reconciling a mention with an unmatched person
  is left to the human researcher (or a future explicit tool), never done implicitly.

#### Records, mentions & links (10) — `Tools/ResearchTools.cs`

| Tool | Params | Returns |
|---|---|---|
| `add_research_record` | `title`, `recordType`, `recordText?`, `transcription?`, `recordDateText?`, `recordYearFrom?`, `recordYearTo?`, `placeText?`, `churchText?`, `archiveName?`, `fond?`, `opis?`, `sprava?`, `page?`, `citationText?`, `sourceUrl?`, `status?` (default `inbox`), `tree?` | `sourceRecordId`, `status`, `createdAt`. |
| `get_research_record` | `sourceRecordId` | The full Evidence Inbox card: `record`, `keywords[]`, `personMentions[]`, `placeMentions[]`, `linkCandidates[]`, `attachments[]` (metadata only — bytes via `get_research_attachment`). |
| `update_research_record` | `sourceRecordId`, plus every field `add_research_record` accepts (all optional; a null field is unchanged) | `sourceRecordId`, `status`, `updatedAt`. |
| `search_research_records` | `status?`, `recordType?`, `query?` (free text), `surname?`, `givenName?`, `place?`, `archiveName?`, `fond?`, `opis?`, `sprava?`, `page?`, `yearFrom?`, `yearTo?`, `keyword?`, `role?`, `linked?` (true/false/omit), `topN?` (default 50), `tree?` | `results[]` + `totalCount`. Every filter besides `tree` is optional and additive — an absent filter never restricts the result set. |
| `add_record_person_mention` | `sourceRecordId`, `nameText?`, `givenName?`, `surname?`, `patronymic?`, `sex?`, `role?`, `ageText?`, `estimatedBirthYear?`, `socialStatus?`, `relationshipText?`, `personMentionId?` (supply to correct an existing mention instead of inserting a new one) | `personMentionId`, `sourceRecordId`. A new mention always starts `unlinked`. |
| `add_record_place_mention` | `sourceRecordId`, `placeText`, `placeType?`, `normalizedName?`, `placeId?` (optional link to a `genealogy.place` row) | `placeMentionId`, `sourceRecordId`. |
| `add_research_keyword` | `sourceRecordId`, `keyword`, `keywordType` (`surname`/`given_name`/`place`/`church`/`archive`/`role`/`record_type`/`free_text`) | `{ ok: true }`. Re-adding an existing `(keyword, keywordType)` pair is a harmless no-op. |
| `suggest_record_person_links` | `personMentionId`, `topN?` (default 10) | `candidates[]` (`personLinkCandidateId`, `personId`, `fullName`, `score`, `explanation`), ranked. **Read-only against the tree** — persists candidates to `research.person_link_candidate` only. |
| `accept_record_person_link` | `personLinkCandidateId` | `personMentionId`, `personId`, `status: "accepted"`. Marks the candidate accepted, sets the mention's `acceptedPersonId`/status/confidence, and supersedes the mention's other still-`suggested` candidates. |
| `reject_record_person_link` | `personLinkCandidateId` | `personLinkCandidateId`, `status: "rejected"`. The row is kept (not deleted) so `suggest_record_person_links` never resurrects the same (mention, person) pair. Never touches the mention's accepted link. |

#### Binary attachments (4) — `Tools/ResearchAttachmentTools.cs`

| Tool | Params | Returns |
|---|---|---|
| `add_research_attachment` | `sourceRecordId`, exactly one of `filePath` / `base64Content`, `fileName?`, `caption?`, `attachmentType?` (`image`/`document`/omit for `other`), `sequenceNo?` (default 0), `sourceUrl?` | `sourceRecordAttachmentId`, `attachmentContentId`, `contentHash`, `byteLength`, `mimeType`, `deduplicated`. |
| `get_research_attachment` | `sourceRecordAttachmentId` | `fileName`, `caption`, `mimeType`, `byteLength`, `contentHash`, `base64Content` — byte-identical to what was stored. |
| `list_research_attachments` | `sourceRecordId` | `attachments[]`, metadata only (no bytes), ordered by `sequenceNo`. |
| `delete_research_attachment` | `sourceRecordAttachmentId` | `sourceRecordAttachmentId`, `deleted`. Deletes only the record's **link** to the content. |

**Attachment rules** (enforced in `AttachmentRepository`/`MimeSniffer`, `Genealogy.Workspace.Data/Research/`):

- **PostgreSQL is the durable store.** Attachment bytes live in `research.attachment_content.content`
  (`bytea`) — there is no filesystem/blob-store dependency, so an attachment survives exactly as long
  as the database does. `scripts/backup.sh`/`restore.sh` back up and restore the whole database with
  no schema filter, so attachments are covered automatically; `scripts/smoke.sh` seeds a
  `source_record` + `attachment_content` row and asserts both survive a backup → tamper → restore
  cycle byte-identical (Phase 6 Task 5 exit criterion, plan §10 step 8).
- **Server-computed, never client-trusted.** The server always derives the MIME type from the bytes'
  magic numbers, computes their SHA-256 hash, and measures their length — a client-declared content
  type or file extension is never trusted for either storage or validation.
- **MIME allowlist:** PNG, JPEG, GIF, WEBP, TIFF, PDF only (`MimeSniffer`). Anything else is rejected
  with `AttachmentTypeNotAllowedException`. Content is never executed or interpreted — only
  classified.
- **Dedup by content hash.** `research.attachment_content` is a GLOBAL table (no `tree_id`) with a
  UNIQUE `content_hash`; identical bytes are stored exactly once and shared across records/trees
  (`add_research_attachment` reports `deduplicated: true` when it reuses existing content).
- **Size limits**, read from environment at process start (`AttachmentOptions.FromEnvironment()`):

  | Variable | Default | Bounds |
  |---|---|---|
  | `GENEALOGY_ATTACHMENT_MAX_BYTES` | 20 MB | A single attachment's raw bytes (local-path import). |
  | `GENEALOGY_ATTACHMENT_MAX_RECORD_BYTES` | 100 MB | Total DISTINCT content linked to any one source record. |
  | `GENEALOGY_ATTACHMENT_MAX_BASE64_BYTES` | 8 MB | Decoded length of a base64 payload — tighter than the file-path ceiling because base64 rides the MCP JSON-RPC channel. |

  An unparseable value is captured and surfaced verbatim by `AttachmentOptions.Validate()` rather than
  silently falling back to the default.
- **Link delete + controlled orphan cleanup.** `delete_research_attachment` removes only the
  `source_record_attachment` link row; the shared `attachment_content` bytes are NEVER cascaded away
  by a link delete (they may be referenced by other records). Reclaiming truly unreferenced content is
  a separate, explicit operation (`AttachmentRepository.CleanupOrphanedContentAsync`) — not currently
  wrapped as an MCP tool — that deletes every `attachment_content` row no link references, and is
  never invoked as a side effect of any other call.

## Examples

These are **real** calls against the sample tree produced by `./scripts/quickstart.sh`
(the bundled `phase0_baseline.ged`: 28 persons across four generations of a
`Тестенко` / `Прикладів` / `Kowalski` family). Every tool returns a `Task<string>` of
JSON; the server emits Unicode escaped (e.g. `Гнат`), shown decoded
here for readability. Tree-scoped tools take an optional `tree` (name or GUID) and fall
back to the default tree when omitted; a name that matches several people comes back as
`{ "error": "...", "candidates": [...] }` rather than a silent pick.

### Exploring a tree

**List trees** — `list_tree_datasets` (no arguments):

```json
{ "trees": [ { "treeId": "b5602b96-…", "name": "Quickstart", "isDefault": false,
              "rootPersonId": null, "createdAt": "2026-07-16T10:21:35Z" } ] }
```

**Find a person** — `find_tree_person { "name": "Тестенко", "tree": "Quickstart", "maxResults": 6 }`:

```json
{
  "tree": { "treeId": "b5602b96-…", "name": "Quickstart", "isDefault": false },
  "count": 6,
  "people": [
    { "personId": "53ce208c-…", "fullName": "Гнат Романович Тестенко",
      "sex": "M", "isLiving": false, "birthYear": 1832, "birthPlace": "вербівка", "deathYear": 1900 },
    { "personId": "a638de63-…", "fullName": "Максим Іванович Тестенко",
      "sex": "M", "isLiving": null, "birthYear": 1903, "birthPlace": "вербівка", "deathYear": null }
    // …4 more
  ]
}
```
`find_tree_person` also accepts `father` / `mother` / `child` / `spouse` / `place` /
`yearFrom` / `yearTo` constraints (all substring / range filters).

**Ancestors** — `get_ancestors { "person": "Максим Іванович Тестенко", "tree": "Quickstart", "maxGenerations": 4 }`
returns `generation`-numbered rows with birth/death years and places:

```json
{
  "person": "Максим Іванович Тестенко", "maxGenerations": 4, "count": 6,
  "ancestors": [
    { "fullName": "Іван Тестенко",              "generation": 1, "birthYear": 1879 },
    { "fullName": "Олена Пилипівна Прикладів",  "generation": 1, "birthYear": 1880 },
    { "fullName": "Северин Гнатович Тестенко",  "generation": 2, "birthYear": 1856,
      "birthDateRaw": "12 MAR 1856", "deathYear": 1920, "deathPlace": "Вербівка" },
    { "fullName": "Гнат Романович Тестенко",    "generation": 3, "birthYear": 1832,
      "birthDateRaw": "ABT 1832" }
    // …
  ]
}
```
Note `birthDateRaw` preserves the original GEDCOM date text (`ABT 1832`, `12 MAR 1856`)
alongside the parsed `birthYear`. `get_descendants`, `get_descendants_by_year`, and the
generic `get_person_tree` (with `direction: "ancestors" | "descendants"`) mirror this shape.

**Family context** — `get_person_family_context { "person": "Гнат Романович Тестенко", "tree": "Quickstart" }`
returns the person plus five sections — `events`, `parents`, `siblings`, `marriages`, `children`:

```json
{
  "person": { "personId": "53ce208c-…", "fullName": "Гнат Романович Тестенко", "sex": "M", "isLiving": false },
  "events": [
    { "eventType": "BIRT", "dateRaw": "ABT 1832", "yearFrom": 1832, "placeRaw": "Вербівка", "isDerived": false },
    { "eventType": "MARR", "dateRaw": "5 JUN 1855", "yearFrom": 1855, "familyId": "a2607903-…", "isDerived": true },
    { "eventType": "DEAT", "dateRaw": "BET 1900 AND 1905", "yearFrom": 1900, "yearTo": 1905 }
    // …CHILD_BIRTH derived events omitted
  ],
  "marriages": [
    { "familyId": "a2607903-…", "spouseName": "Килина Тимофіївна Прикладів",
      "spouseBirthYear": 1834, "marriageYear": 1855, "marriagePlaceRaw": "Вербівка" }
  ],
  "children": [
    { "fullName": "Северин Гнатович Тестенко", "birthYear": 1856, "otherParentName": "Килина Тимофіївна Прикладів" }
    // …Одарка, Явдоха, Роксолана
  ]
}
```

**Closest common ancestor** — `get_closest_common_ancestor { "name1": "Максим Іванович Тестенко", "name2": "Роксолана Гнатівна Тестенко", "tree": "Quickstart" }`
resolves each name, then reports the shared ancestor and each person's depth (note the
**asymmetric** depths — the tool handles endogamy/uneven generations correctly):

```json
{
  "inputs": [
    { "name": "Максим Іванович Тестенко",  "resolvedId": "a638de63-…" },
    { "name": "Роксолана Гнатівна Тестенко", "resolvedId": "68317377-…" }
  ],
  "ancestor": { "ancestorName": "Килина Тимофіївна Прикладів", "maxDepth": 3, "sumDepth": 4, "personCount": 2 },
  "personDepths": [
    { "personName": "Роксолана Гнатівна Тестенко", "depth": 1 },
    { "personName": "Максим Іванович Тестенко",    "depth": 3 }
  ]
}
```
`get_path_between_persons` gives the step-by-step relationship path between two people.

### Workflow: import a GEDCOM (staged, reviewed, applied)

Nothing touches the production tree until you explicitly apply. Typical sequence:

1. `stage_gedcom_import { "filePath": "/path/to/family.ged", "tree": "Rudenko" }` — runs the
   parser and loads the parsed rows into staging; returns an `importBatchId` + per-table row counts.
2. `get_gedcom_import_preview { "importBatchId": "…" }` — ADD / UPDATE / MISSING_FROM_IMPORT
   counts per entity, plus a sample of person-level changes, computed against the current tree.
3. `find_import_duplicate_candidates { "importBatchId": "…" }` and
   `get_gedcom_import_readiness_report { "importBatchId": "…" }` — surface probable duplicates
   (scored, with evidence) and the readiness gates (high-confidence duplicates → `blocker`,
   name/date issues → `warning`).
4. Resolve duplicates: `get_duplicate_candidate_detail { … }` then
   `reject_tree_person_merge_candidate { … }` for false matches.
5. `apply_gedcom_import { "importBatchId": "…", "dryRun": false }` — transactional, idempotent
   apply. **`dryRun` defaults to `true`** (returns the preview and changes nothing); pass
   `false` to actually write. `deleteMissing` defaults to `false`.
6. `list_pending_gedcom_imports` / `cancel_gedcom_import { "importBatchId": "…" }` manage
   batches you don't apply.

`./scripts/quickstart.sh` runs the equivalent of steps 1–2 and 5 non-interactively.

### Workflow: capture evidence and link it to the tree

The Evidence Inbox stores archival records (with binary attachments) separately from the
tree; a mention is linked to a tree person only when you explicitly accept a suggestion —
nothing auto-creates or auto-modifies tree persons.

1. `add_research_record { "tree": "Rudenko", "title": "1858 birth — Одарка Тестенко",
   "recordType": "birth", "archiveName": "ДАХмО", "fond": "315", "opis": "1", "sprava": "42",
   "page": "12зв", "transcription": "…" }` → `{ "sourceRecordId": "…", "status": "inbox" }`.
2. `add_research_attachment { "sourceRecordId": "…", "filePath": "/scans/page12.jpg" }`
   (or `"base64Content": "…"`) — server computes SHA-256, verifies the MIME type from magic
   bytes, dedups by hash, and stores the bytes in PostgreSQL. Returns
   `{ "contentHash": "…", "byteLength": …, "mimeType": "image/jpeg", "deduplicated": false }`.
3. `add_record_person_mention { "sourceRecordId": "…", "surname": "Тестенко",
   "givenName": "Одарка", "role": "child", "estimatedBirthYear": 1858 }`.
4. `search_research_records { "tree": "Rudenko", "surname": "Тестенко", "yearFrom": 1855,
   "yearTo": 1860, "archiveName": "ДАХмО" }` — find records by surname, place, year, archive
   citation, keyword, free text, or `linked` state.
5. `suggest_record_person_links { "personMentionId": "…" }` — scores candidate tree persons
   against the mention (surname / given name / estimated birth year), persists them, and
   returns them ranked, **without modifying the tree**. Real output for a `surname: "Тестенко",
   givenName: "Одарка", estimatedBirthYear: 1858` mention against the sample tree — the exact
   match rises to the top, weaker surname-only matches trail behind:
   ```json
   { "candidates": [
       { "personId": "cad6780e-…", "fullName": "Одарка Гнатівна Тестенко", "score": 0.85,
         "explanation": "surname 'Тестенко' matches 'тестенко' exactly (+0.50); given name 'Одарка' partially matches 'Одарка Гнатівна' (+0.15); birth year 1858 vs 1858 (Δ0, +0.20)" },
       { "personId": "baadf667-…", "fullName": "Северин Гнатович Тестенко", "score": 0.60,
         "explanation": "surname … exactly (+0.50); birth year 1858 vs 1856 (Δ2, +0.10)" }
       // …surname-only matches at 0.50
   ] }
   ```
6. `accept_record_person_link { "personLinkCandidateId": "…" }` (or
   `reject_record_person_link { … }` — rejected candidates are not re-suggested). Accepting
   records the link on the mention; it still does not alter the tree person.

## Configuration (environment variables)

The server reads its PostgreSQL connection from `GENEALOGY_DB_*` environment variables (via
`WorkspaceDbOptions.FromEnvironment()` in `Genealogy.Workspace.Data`) — **not** from an
`appsettings.json` connection string, unlike the SQL Server MCP server.

| Variable | Default | Notes |
|---|---|---|
| `GENEALOGY_DB_HOST` | `127.0.0.1` | |
| `GENEALOGY_DB_PORT` | `5432` | Must parse as 1–65535. |
| `GENEALOGY_DB_DATABASE` | `genealogy_workspace` | |
| `GENEALOGY_DB_USERNAME` | `genealogy` | |
| `GENEALOGY_DB_PASSWORD` | — | Required, no default. |

The published `run.sh` wrapper (see below) is where these are normally set for a given MCP client
registration.

## Build / publish

The server targets `net10.0` and is published as a framework-dependent artifact, mirroring the
existing SQL Server MCP server's `./Scripts/publish_mcp.sh` convention:

```bash
./Genealogy.Workspace/scripts/publish_mcp.sh
```

This builds `src/Genealogy.Workspace.McpServer/Genealogy.Workspace.McpServer.csproj` in `Release`,
publishes it to `publish/GenealogyMcp/` at the repository root, and writes an executable
`publish/GenealogyMcp/run.sh` wrapper that exports the five `GENEALOGY_DB_*` variables (edit the
script's defaults, or export them in your shell before running it, to point at a non-default
database) and then launches the published DLL.

After any change to the MCP tool code, re-run the publish script — `.mcp.json` points at the
published `run.sh`, not at `dotnet build`'s own output, so a plain build alone will not pick up the
change for a connected MCP client.

## Registering with an MCP client (`.mcp.json`)

The repository root `.mcp.json` registers this server alongside the existing `dna-analysis` (SQL
Server) entry:

```json
{
  "mcpServers": {
    "dna-analysis": {
      "command": "/Users/dmytrorudenko/Projects/DNA-DB/publish/DnaMcp/run.sh",
      "args": []
    },
    "genealogy-workspace": {
      "command": "/Users/dmytrorudenko/Projects/DNA-DB/publish/GenealogyMcp/run.sh",
      "args": []
    }
  }
}
```

Both servers can be connected at once — they operate on entirely separate databases (SQL Server vs.
the local PostgreSQL workspace) and expose disjoint tool sets.
