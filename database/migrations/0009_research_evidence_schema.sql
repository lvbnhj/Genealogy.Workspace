-- 0009_research_evidence_schema.sql
--
-- Evidence Inbox schema for the PostgreSQL genealogy workspace. Creates the
-- source-record, attachment, keyword, mention and link-candidate tables that
-- back the Evidence Inbox MVP (plan section 8). This is a product-neutral
-- genealogy feature: it captures archival/vital records (birth, marriage,
-- death, confession, revision, metric-book, ...), their attachments and the
-- people/places they mention, and lets a mention be linked to a person in the
-- production tree. It carries NO DNA data.
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or extend
-- the schema by adding a new, higher-numbered migration instead of changing an
-- existing one. Editing an applied migration breaks the schema_version journal
-- and produces divergent databases.
--
-- The `research` (and `genealogy`) schemas already exist from migration
-- 0001_create_schemas.sql. This migration only creates objects inside the
-- existing `research` schema; it does NOT re-create schemas.
--
-- Integrity model (plan section 7, mirrored from 0002_genealogy_core_schema):
-- tree ownership that a plain foreign key cannot express is enforced with
-- composite unique keys plus composite foreign keys. `source_record` carries
-- UNIQUE (tree_id, source_record_id); every same-tree edge that must stay
-- inside a record's tree references that composite key, and any accepted or
-- candidate link to a `genealogy.person` references genealogy.person
-- (tree_id, person_id) so a link can never point at a person in another tree.
--
-- Three invariants this schema guarantees at the database level:
--   * DEDUP: attachment bytes are content-addressed. attachment_content is
--     GLOBAL (no tree_id) and content_hash (SHA-256) is UNIQUE, so identical
--     bytes are stored exactly once and shared across records/trees.
--   * CASCADE ASYMMETRY: deleting a source_record cascades to its attachment
--     LINKS, keywords, person mentions and place mentions (and, transitively,
--     link candidates), but NEVER to attachment_content. The shared/deduped
--     bytes survive link deletion; unreferenced content is reclaimed by a
--     controlled cleanup operation, not an unsafe immediate cascade.
--   * SAME-TREE LINKING: an accepted mention link and a person_link_candidate
--     can only target a genealogy.person in the SAME tree as the evidence
--     record; the composite FK rejects any cross-tree link.
--
-- source_url columns are provenance only (where a record/attachment came from)
-- and are NEVER used as durable storage: the durable copy of attachment bytes
-- is attachment_content.content.

-- ---------------------------------------------------------------------------
-- source_record  [Evidence Inbox root: one archival/vital record]
-- ---------------------------------------------------------------------------
create table research.source_record (
    source_record_id uuid primary key,
    tree_id          uuid not null references genealogy.tree (tree_id),
    title            text not null,
    record_type      text not null
        check (record_type in ('birth', 'marriage', 'death', 'confession',
                               'revision', 'metric_book', 'other')),
    record_text      text null,
    transcription    text null,
    record_date_text text null,
    record_date_from date null,
    record_date_to   date null,
    record_year_from smallint null,
    record_year_to   smallint null,
    place_text       text null,
    church_text      text null,
    archive_name     text null,
    fond             text null,
    opis             text null,
    sprava           text null,
    page             text null,
    citation_text    text null,
    source_url       text null,          -- provenance only, NEVER durable storage
    status           text not null default 'inbox'
        check (status in ('inbox', 'in_review', 'resolved', 'dismissed', 'archived')),
    created_at       timestamptz not null default now(),
    updated_at       timestamptz null,
    -- Composite target for same-tree foreign keys from mentions/candidates.
    constraint uq_source_record_tree unique (tree_id, source_record_id)
);

create index ix_source_record_tree_status
    on research.source_record (tree_id, status);
create index ix_source_record_tree_type
    on research.source_record (tree_id, record_type);
create index ix_source_record_tree_years
    on research.source_record (tree_id, record_year_from, record_year_to);

-- ---------------------------------------------------------------------------
-- attachment_content  (GLOBAL — no tree_id; content-addressed by hash)
-- The durable copy of attachment bytes. Deduplicated by content_hash so the
-- same bytes are stored once and shared. Deliberately NOT cascaded from
-- source_record_attachment (see cascade-asymmetry note in the header).
-- ---------------------------------------------------------------------------
create table research.attachment_content (
    attachment_content_id bigint generated always as identity primary key,
    content_hash          char(64) not null unique,    -- SHA-256 hex; dedup key
    content               bytea not null,
    byte_length           bigint not null,
    mime_type             text not null,               -- server-verified
    created_at            timestamptz not null default now()
);

-- ---------------------------------------------------------------------------
-- source_record_attachment  [links a source_record to shared content bytes]
-- attachment_content_id has NO on delete cascade on purpose: content is
-- shared/deduped and must survive deletion of any one link to it.
-- ---------------------------------------------------------------------------
create table research.source_record_attachment (
    source_record_attachment_id bigint generated always as identity primary key,
    source_record_id            uuid not null
        references research.source_record (source_record_id) on delete cascade,
    attachment_content_id       bigint not null
        references research.attachment_content (attachment_content_id),
    file_name                   text null,
    caption                     text null,
    sequence_no                 integer not null default 0,
    attachment_type             text not null default 'other'
        check (attachment_type in ('image', 'document', 'other')),
    source_url                  text null,             -- optional provenance
    created_at                  timestamptz not null default now(),
    constraint uq_source_record_attachment
        unique (source_record_id, attachment_content_id, sequence_no)
);

create index ix_source_record_attachment_record
    on research.source_record_attachment (source_record_id, sequence_no);

-- ---------------------------------------------------------------------------
-- source_record_keyword  [denormalized search keywords for a source_record]
-- ---------------------------------------------------------------------------
create table research.source_record_keyword (
    source_record_id uuid not null
        references research.source_record (source_record_id) on delete cascade,
    keyword          text not null,
    keyword_type     text not null
        check (keyword_type in ('surname', 'given_name', 'place', 'church',
                               'archive', 'role', 'record_type', 'free_text')),
    constraint pk_source_record_keyword
        primary key (source_record_id, keyword_type, keyword)
);

create index ix_source_record_keyword_type_keyword
    on research.source_record_keyword (keyword_type, keyword);

-- ---------------------------------------------------------------------------
-- record_person_mention  [a person named in a source_record]
-- tree_id is denormalized from the record so the two composite FKs below can
-- enforce (a) the mention stays with its record's tree and (b) any accepted
-- link targets a person in that SAME tree.
-- ---------------------------------------------------------------------------
create table research.record_person_mention (
    person_mention_id    uuid primary key,
    tree_id              uuid not null,   -- denormalized from the record
    source_record_id     uuid not null,
    name_text            text null,
    given_name           text null,
    surname              text null,
    patronymic           text null,
    sex                  char(1) null check (sex in ('M', 'F', 'U')),
    role                 text null
        check (role is null or role in ('child', 'father', 'mother', 'spouse',
                                        'widow', 'witness', 'priest',
                                        'household_member', 'other')),
    age_text             text null,
    estimated_birth_year smallint null,
    social_status        text null,
    relationship_text    text null,
    status               text not null default 'unlinked'
        check (status in ('unlinked', 'suggested', 'accepted', 'rejected')),
    accepted_person_id   uuid null,       -- currently-accepted tree link
    confidence           numeric(5, 4) null
        check (confidence is null or (confidence >= 0.0 and confidence <= 1.0)),
    created_at           timestamptz not null default now(),
    updated_at           timestamptz null,
    -- Ties the mention to its record AND enforces the same tree.
    constraint fk_mention_record
        foreign key (tree_id, source_record_id)
        references research.source_record (tree_id, source_record_id)
        on delete cascade,
    -- Accepted link must be a person in the SAME tree (composite FK allows a
    -- NULL accepted_person_id).
    constraint fk_mention_accepted_person
        foreign key (tree_id, accepted_person_id)
        references genealogy.person (tree_id, person_id)
);

create index ix_record_person_mention_record
    on research.record_person_mention (source_record_id);
create index ix_record_person_mention_tree_surname
    on research.record_person_mention (tree_id, surname);
create index ix_record_person_mention_status
    on research.record_person_mention (status);

-- ---------------------------------------------------------------------------
-- record_place_mention  [a place named in a source_record]
-- place_id is an optional link to the GLOBAL genealogy.place table (no tree
-- scope), mirroring how genealogy.event/family reference places.
-- ---------------------------------------------------------------------------
create table research.record_place_mention (
    place_mention_id uuid primary key,
    source_record_id uuid not null
        references research.source_record (source_record_id) on delete cascade,
    place_text       text not null,
    place_type       text null
        check (place_type is null or place_type in ('village', 'town', 'church',
                                                    'parish', 'district',
                                                    'region', 'archive', 'other')),
    normalized_name  text null,
    place_id         bigint null references genealogy.place (place_id),
    created_at       timestamptz not null default now()
);

create index ix_record_place_mention_record
    on research.record_place_mention (source_record_id);
create index ix_record_place_mention_normalized
    on research.record_place_mention (normalized_name);

-- ---------------------------------------------------------------------------
-- person_link_candidate  [a proposed link from a mention to a tree person]
-- tree_id is denormalized so the composite FK forces the candidate target to
-- be a person in the same tree as the mention/record.
-- ---------------------------------------------------------------------------
create table research.person_link_candidate (
    person_link_candidate_id uuid primary key,
    person_mention_id        uuid not null
        references research.record_person_mention (person_mention_id) on delete cascade,
    tree_id                  uuid not null,   -- denormalized for same-tree FK
    person_id                uuid not null,
    score                    numeric(5, 4) not null
        check (score >= 0.0 and score <= 1.0),
    explanation              text null,
    status                   text not null default 'suggested'
        check (status in ('suggested', 'accepted', 'rejected', 'superseded')),
    created_at               timestamptz not null default now(),
    decided_at               timestamptz null,
    -- Candidate target must be a person in the same tree.
    constraint fk_candidate_person
        foreign key (tree_id, person_id)
        references genealogy.person (tree_id, person_id)
);

create index ix_person_link_candidate_mention_status
    on research.person_link_candidate (person_mention_id, status);
create index ix_person_link_candidate_tree_person
    on research.person_link_candidate (tree_id, person_id);
