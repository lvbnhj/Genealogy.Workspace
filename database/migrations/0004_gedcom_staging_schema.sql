-- 0004_gedcom_staging_schema.sql
--
-- GEDCOM import STAGING tables in the existing `genealogy` schema for the
-- PostgreSQL genealogy workspace. These tables hold the intermediate,
-- per-import-batch representation of a parsed GEDCOM file before it is
-- reviewed and applied into the production `genealogy.*` tables created by
-- migration 0002.
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or extend
-- the schema by adding a new, higher-numbered migration instead of changing an
-- existing one. Editing an applied migration breaks the schema_version journal
-- and produces divergent databases.
--
-- The `genealogy` schema already exists from migration 0001_create_schemas.sql
-- and already holds the production tables from 0002. This migration only adds
-- new staging tables inside the existing schema.
--
-- Ported (snake_case) from these SQL Server `ged.GedcomImport*` source tables:
--   ged.GedcomImportBatch            -> genealogy.gedcom_import_batch
--   ged.GedcomImportPerson           -> genealogy.gedcom_import_person
--   ged.GedcomImportPersonName       -> genealogy.gedcom_import_person_name
--   ged.GedcomImportPersonNameParsed -> genealogy.gedcom_import_person_name_parsed
--   ged.GedcomImportPlace            -> genealogy.gedcom_import_place
--   ged.GedcomImportFamily           -> genealogy.gedcom_import_family
--   ged.GedcomImportFamilyChild      -> genealogy.gedcom_import_family_child
--   ged.GedcomImportParentOf         -> genealogy.gedcom_import_parent_of
--   ged.GedcomImportSpouseOf         -> genealogy.gedcom_import_spouse_of
--   ged.GedcomImportEvent            -> genealogy.gedcom_import_event
--   ged.GedcomImportEventCitation    -> genealogy.gedcom_import_event_citation
--   ged.GedcomImportDateWarning      -> genealogy.gedcom_import_date_warning
--
-- Deliberately NOT ported here: ged.GedcomImportDuplicateCandidate (Phase 4).
--
-- Every staging table other than the batch root carries
-- `import_batch_id uuid not null references genealogy.gedcom_import_batch
-- (import_batch_id) on delete cascade`, so deleting a batch removes all of its
-- staged rows in one statement.
--
-- Composite foreign keys are used where a child staging row must be tied to a
-- specific row of another staging table within the same batch (e.g. parsed
-- names to raw names, citations and date warnings to events). This mirrors
-- the plan's same-scope ownership pattern used for production tables in 0002.

-- ---------------------------------------------------------------------------
-- gedcom_import_batch  [from ged.GedcomImportBatch]  -- staging root
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_batch (
    import_batch_id      uuid primary key,
    source_file_path     text not null,
    source_file_hash     varchar(64) null,
    tree_id              uuid not null references genealogy.tree (tree_id),
    root_external_id     varchar(80) null,
    root_person_id       uuid null,
    person_count         integer null,
    family_count         integer null,
    event_count          integer null,
    place_count          integer null,
    scope_invalid_count  integer null,
    status               varchar(30) not null default 'STAGED'
        check (status in ('STAGED', 'PREVIEWED', 'WAITING_FOR_CONFIRMATION',
                          'APPLIED', 'FAILED', 'CANCELLED', 'ABANDONED',
                          'ARCHIVED')),
    notes                text null,
    created_at           timestamptz not null default now(),
    previewed_at         timestamptz null,
    applied_at           timestamptz null,
    cancelled_at         timestamptz null
);

create index ix_gedcom_import_batch_tree on genealogy.gedcom_import_batch (tree_id);
create index ix_gedcom_import_batch_status on genealogy.gedcom_import_batch (status);

-- ---------------------------------------------------------------------------
-- gedcom_import_person  [from ged.GedcomImportPerson]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_person (
    import_batch_id       uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    tree_person_id        uuid not null,
    external_id           varchar(80) null,
    -- No CHECK on sex here (unlike production genealogy.person in 0002): staging
    -- must accept whatever the parser produced so non-M/F values surface as
    -- reviewable data in preview/readiness rather than hard-failing the load.
    -- The M/F domain is enforced at apply time against production (Phase 4).
    sex                   char(1) null,
    is_living             boolean null,
    primary_display_name  text null,
    surname_normalized    text null,
    constraint pk_gedcom_import_person primary key (import_batch_id, tree_person_id)
);

create index ix_gedcom_import_person_surname
    on genealogy.gedcom_import_person (import_batch_id, surname_normalized);

-- ---------------------------------------------------------------------------
-- gedcom_import_person_name  (raw names)  [from ged.GedcomImportPersonName]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_person_name (
    import_batch_id       uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    row_number            integer not null,
    tree_person_id        uuid not null,
    script_code           varchar(10) not null,
    name_type             varchar(30) not null,        -- free text, no check
    given                 text null,
    surname               text null,
    full_name             text not null,
    full_name_normalized  text not null,
    is_primary            boolean not null,
    constraint pk_gedcom_import_person_name primary key (import_batch_id, row_number)
);

create index ix_gedcom_import_person_name_tree_person
    on genealogy.gedcom_import_person_name (import_batch_id, tree_person_id);

-- ---------------------------------------------------------------------------
-- gedcom_import_person_name_parsed  [from ged.GedcomImportPersonNameParsed]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_person_name_parsed (
    import_batch_id           uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    row_number                integer not null,
    source_name_row_number    integer not null,
    tree_person_id            uuid not null,
    raw_name                  text not null,
    name_type                 varchar(30) null,
    script_code               varchar(10) null,
    given_name                text null,
    patronymic                text null,
    surname                   text null,
    maiden_surname            text null,
    married_surname           text null,
    title_prefix              text null,
    suffix                    text null,
    language_hint             varchar(20) null,
    given_name_normalized     text null,
    patronymic_normalized     text null,
    surname_normalized        text null,
    full_name_normalized      text not null,
    name_tokens               text null,
    variant_explanation       text null,
    normalization_confidence  numeric(5, 4) not null
        check (normalization_confidence >= 0.0 and normalization_confidence <= 1.0),
    parser_status             varchar(30) not null
        check (parser_status in ('OK', 'LOW_CONFIDENCE', 'AMBIGUOUS', 'UNNAMED',
                                 'MISSING_NAME')),
    created_at                timestamptz not null default now(),
    constraint pk_gedcom_import_person_name_parsed primary key (import_batch_id, row_number),
    constraint fk_gedcom_import_person_name_parsed_source
        foreign key (import_batch_id, source_name_row_number)
        references genealogy.gedcom_import_person_name (import_batch_id, row_number)
);

create index ix_gedcom_import_person_name_parsed_status_conf
    on genealogy.gedcom_import_person_name_parsed
    (import_batch_id, parser_status, normalization_confidence);
create index ix_gedcom_import_person_name_parsed_full_name
    on genealogy.gedcom_import_person_name_parsed (import_batch_id, full_name_normalized);
create index ix_gedcom_import_person_name_parsed_surname
    on genealogy.gedcom_import_person_name_parsed (import_batch_id, surname_normalized);

-- ---------------------------------------------------------------------------
-- gedcom_import_place  [from ged.GedcomImportPlace]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_place (
    import_batch_id    uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    row_number         integer not null,
    place_raw          text not null,
    place_normalized   text null,
    constraint pk_gedcom_import_place primary key (import_batch_id, row_number)
);

-- ---------------------------------------------------------------------------
-- gedcom_import_family  [from ged.GedcomImportFamily]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_family (
    import_batch_id           uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    family_id                 uuid not null,
    external_id               varchar(80) null,
    spouse1_tree_person_id    uuid null,
    spouse2_tree_person_id    uuid null,
    marriage_date_raw         text null,
    marriage_year             smallint null,
    marriage_place_raw        text null,
    notes                     text null,
    constraint pk_gedcom_import_family primary key (import_batch_id, family_id)
);

-- ---------------------------------------------------------------------------
-- gedcom_import_family_child  [from ged.GedcomImportFamilyChild]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_family_child (
    import_batch_id      uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    family_id            uuid not null,
    child_tree_person_id uuid not null,
    constraint pk_gedcom_import_family_child
        primary key (import_batch_id, family_id, child_tree_person_id)
);

-- ---------------------------------------------------------------------------
-- gedcom_import_parent_of  [from ged.GedcomImportParentOf]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_parent_of (
    import_batch_id        uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    parent_tree_person_id  uuid not null,
    child_tree_person_id   uuid not null,
    relation_type          varchar(20) not null,        -- free text, no check
    constraint pk_gedcom_import_parent_of
        primary key (import_batch_id, parent_tree_person_id, child_tree_person_id, relation_type)
);

-- ---------------------------------------------------------------------------
-- gedcom_import_spouse_of  [from ged.GedcomImportSpouseOf]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_spouse_of (
    import_batch_id      uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    row_number           integer not null,
    from_tree_person_id  uuid not null,
    to_tree_person_id    uuid not null,
    relation_type        varchar(20) not null,        -- free text, no check
    family_id            uuid null,
    marriage_year        smallint null,
    marriage_place_raw   text null,
    constraint pk_gedcom_import_spouse_of primary key (import_batch_id, row_number)
);

-- ---------------------------------------------------------------------------
-- gedcom_import_event  [from ged.GedcomImportEvent]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_event (
    import_batch_id        uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    row_number              integer not null,
    external_event_key      varchar(120) not null,
    tree_person_id          uuid not null,
    event_type              varchar(30) not null,        -- free text, no check
    event_value             text null,
    date_raw                text null,
    date_from               date null,
    date_to                 date null,
    year_from               smallint null,
    year_to                 smallint null,
    place_raw               text null,
    place_normalized        text null,
    family_id               uuid null,
    related_tree_person_id  uuid null,
    is_derived              boolean not null,
    notes                   text null,
    constraint pk_gedcom_import_event primary key (import_batch_id, row_number)
);

create index ix_gedcom_import_event_tree_person
    on genealogy.gedcom_import_event (import_batch_id, tree_person_id);
create index ix_gedcom_import_event_family
    on genealogy.gedcom_import_event (import_batch_id, family_id);
create index ix_gedcom_import_event_type_years
    on genealogy.gedcom_import_event (import_batch_id, event_type, year_from, year_to);

-- ---------------------------------------------------------------------------
-- gedcom_import_event_citation  [from ged.GedcomImportEventCitation]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_event_citation (
    import_batch_id    uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    row_number         integer not null,
    event_row_number   integer not null,
    source_ref         text null,
    source_title       text null,
    page               text null,
    quality            varchar(20) null,
    citation_date_raw  text null,
    citation_text      text null,
    note               text null,
    constraint pk_gedcom_import_event_citation primary key (import_batch_id, row_number),
    constraint fk_gedcom_import_event_citation_event
        foreign key (import_batch_id, event_row_number)
        references genealogy.gedcom_import_event (import_batch_id, row_number)
);

create index ix_gedcom_import_event_citation_event
    on genealogy.gedcom_import_event_citation (import_batch_id, event_row_number);

-- ---------------------------------------------------------------------------
-- gedcom_import_date_warning  [from ged.GedcomImportDateWarning]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_date_warning (
    import_batch_id      uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,
    event_row_number     integer not null,
    tree_person_id       uuid null,
    person_xref          varchar(80) null,
    person_name          text null,
    event_type           varchar(30) not null,        -- free text, no check
    date_raw             text null,
    date_from            date null,
    date_to              date null,
    date_precision       varchar(20) null,
    date_modifier        varchar(20) null,
    date_status          varchar(20) null,
    warning_kind         varchar(30) not null
        check (warning_kind in ('UNPARSED', 'APPROXIMATE', 'OPEN_BOUND', 'RANGE')),
    warning_message      text null,
    constraint pk_gedcom_import_date_warning
        primary key (import_batch_id, event_row_number, warning_kind),
    constraint fk_gedcom_import_date_warning_event
        foreign key (import_batch_id, event_row_number)
        references genealogy.gedcom_import_event (import_batch_id, row_number)
);
