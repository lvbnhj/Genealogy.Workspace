-- 0002_genealogy_core_schema.sql
--
-- Production `genealogy` schema: core tree, person, name, place, family,
-- relationship, event and citation tables for the PostgreSQL genealogy
-- workspace.
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or extend
-- the schema by adding a new, higher-numbered migration instead of changing an
-- existing one. Editing an applied migration breaks the schema_version journal
-- and produces divergent databases.
--
-- The `genealogy` (and `research`) schemas already exist from migration
-- 0001_create_schemas.sql. This migration only creates objects inside the
-- existing `genealogy` schema; it does NOT re-create schemas.
--
-- Ported (snake_case) from these SQL Server `ged.*` source tables:
--   ged.TreeDataset        -> genealogy.tree
--   ged.TreePerson         -> genealogy.person          (DROP NameEmbedding)
--   ged.TreePersonNames    -> genealogy.person_name      (DROP NameEmbedding)
--   ged.TreePlace          -> genealogy.place            (DROP PlaceEmbedding; GLOBAL)
--   ged.TreeFamily         -> genealogy.family           (DROP persisted computed cols)
--   ged.TreeFamilyChild    -> genealogy.family_child
--   ged.TreeParentOf       -> genealogy.parent_child      (canonical parent edge)
--   ged.TreeEvent          -> genealogy.event
--   ged.TreeEventCitation  -> genealogy.event_citation
--   ged.NameVariantRule    -> genealogy.name_variant_rule (GLOBAL)
--
-- Deliberately NOT ported: all vector/embedding columns, all DNA columns,
-- ged.TreeRelationship, ged.TreeSpouseOf, ged.TreePersonLink, ged.TreePersonG,
-- ged.TreeNameVariant (reconciled into person_name).
--
-- Integrity model (plan section 7): tree ownership that a plain foreign key
-- cannot express is enforced with composite unique keys plus composite foreign
-- keys. `person` and `family` each carry UNIQUE (tree_id, <id>); every in-tree
-- edge references that composite key so a row can never point at another tree.
-- `place` and `name_variant_rule` are GLOBAL (no tree_id).

-- ---------------------------------------------------------------------------
-- place  (GLOBAL — no tree_id)  [from ged.TreePlace; DROP PlaceEmbedding]
-- ---------------------------------------------------------------------------
create table genealogy.place (
    place_id         bigint generated always as identity primary key,
    place_raw        text not null unique,
    place_normalized text null
);

create index ix_place_normalized on genealogy.place (place_normalized);

-- ---------------------------------------------------------------------------
-- name_variant_rule  (GLOBAL — no tree_id)  [from ged.NameVariantRule]
-- ---------------------------------------------------------------------------
create table genealogy.name_variant_rule (
    name_variant_rule_id bigint generated always as identity primary key,
    rule_type            varchar(30) not null
        check (rule_type in ('given_name', 'surname', 'patronymic',
                             'transliteration', 'spelling', 'title')),
    canonical_value      text not null,
    variant_value        text not null,
    language_code        varchar(20) null,
    script_code          varchar(10) null,
    confidence           numeric(5, 4) not null default 1.0
        check (confidence >= 0.0 and confidence <= 1.0),
    notes                text null,
    is_active            boolean not null default true,
    created_at           timestamptz not null default now(),
    updated_at           timestamptz null,
    constraint uq_name_variant_rule
        unique (rule_type, canonical_value, variant_value)
);

-- ---------------------------------------------------------------------------
-- tree  [from ged.TreeDataset]
-- Created WITHOUT the root_person_id foreign key: person does not exist yet
-- (circular dependency tree <-> person). The same-tree composite root FK is
-- added by ALTER TABLE after person is created (see bottom of file).
-- ---------------------------------------------------------------------------
create table genealogy.tree (
    tree_id        uuid primary key,
    name           text not null unique,
    description    text null,
    root_person_id uuid null,
    is_default     boolean not null default false,
    created_at     timestamptz not null default now(),
    updated_at     timestamptz null
);

-- Only one default tree is allowed.
create unique index uq_tree_one_default
    on genealogy.tree (is_default) where is_default;

-- ---------------------------------------------------------------------------
-- person  [from ged.TreePerson; DROP NameEmbedding]
-- ---------------------------------------------------------------------------
create table genealogy.person (
    person_id            uuid primary key,
    tree_id              uuid not null references genealogy.tree (tree_id),
    external_id          varchar(80) null,
    sex                  char(1) null check (sex in ('M', 'F')),
    is_living            boolean null,
    primary_display_name text null,
    surname_normalized   text null,
    created_at           timestamptz not null default now(),
    updated_at           timestamptz null,
    -- Composite target for every same-tree foreign key.
    constraint uq_person_tree_person unique (tree_id, person_id)
);

-- external_id is unique within a tree when present.
create unique index uq_person_tree_external_id
    on genealogy.person (tree_id, external_id)
    where external_id is not null;

create index ix_person_tree_display_name
    on genealogy.person (tree_id, primary_display_name);
create index ix_person_surname_normalized
    on genealogy.person (surname_normalized);

-- Circular FK resolved: tree root must belong to the same tree.
alter table genealogy.tree
    add constraint fk_tree_root_person
    foreign key (tree_id, root_person_id)
    references genealogy.person (tree_id, person_id);

-- ---------------------------------------------------------------------------
-- person_name  [from ged.TreePersonNames; DROP NameEmbedding]
-- ---------------------------------------------------------------------------
create table genealogy.person_name (
    person_name_id       bigint generated always as identity primary key,
    tree_id              uuid not null,
    person_id            uuid not null,
    script_code          varchar(10) not null,
    name_type            varchar(30) not null,        -- free text, no check
    given                text null,
    surname              text null,
    full_name            text not null,
    full_name_normalized text not null,
    is_primary           boolean not null default false,
    created_at           timestamptz not null default now(),
    constraint fk_person_name_person
        foreign key (tree_id, person_id)
        references genealogy.person (tree_id, person_id),
    constraint uq_person_name_normalized
        unique (person_id, script_code, full_name_normalized)
);

-- One primary name per person (integrity gap absent in the SQL Server source).
create unique index uq_person_name_one_primary
    on genealogy.person_name (person_id) where is_primary;

create index ix_person_name_tree_normalized
    on genealogy.person_name (tree_id, full_name_normalized);
create index ix_person_name_normalized
    on genealogy.person_name (full_name_normalized);
create index ix_person_name_full_name
    on genealogy.person_name (full_name);

-- ---------------------------------------------------------------------------
-- family  [from ged.TreeFamily; DROP SpouseLow/SpouseHigh/MarriageYearKey]
-- ---------------------------------------------------------------------------
create table genealogy.family (
    family_id          uuid primary key,
    tree_id            uuid not null references genealogy.tree (tree_id),
    spouse1_person_id  uuid not null,
    spouse2_person_id  uuid not null,
    marriage_date_raw  text null,
    marriage_year      smallint null,
    marriage_place_raw text null,
    marriage_place_id  bigint null references genealogy.place (place_id),
    notes              text null,
    created_at         timestamptz not null default now(),
    updated_at         timestamptz null,
    -- Spouses must belong to the family's tree.
    constraint fk_family_spouse1
        foreign key (tree_id, spouse1_person_id)
        references genealogy.person (tree_id, person_id),
    constraint fk_family_spouse2
        foreign key (tree_id, spouse2_person_id)
        references genealogy.person (tree_id, person_id),
    -- A family cannot contain the same spouse twice.
    constraint ck_family_distinct_spouses
        check (spouse1_person_id <> spouse2_person_id),
    -- Composite target for every same-tree foreign key.
    constraint uq_family_tree_family unique (tree_id, family_id)
);

-- Replaces the SQL Server persisted computed SpouseLow/SpouseHigh/
-- MarriageYearKey uniqueness: one family per unordered spouse pair + year.
create unique index uq_family_spouse_pair_year
    on genealogy.family (
        tree_id,
        least(spouse1_person_id, spouse2_person_id),
        greatest(spouse1_person_id, spouse2_person_id),
        coalesce(marriage_year, -1)
    );

create index ix_family_spouse1 on genealogy.family (spouse1_person_id);
create index ix_family_spouse2 on genealogy.family (spouse2_person_id);

-- ---------------------------------------------------------------------------
-- family_child  [from ged.TreeFamilyChild]
-- ---------------------------------------------------------------------------
create table genealogy.family_child (
    tree_id         uuid not null,
    family_id       uuid not null,
    child_person_id uuid not null,
    created_at      timestamptz not null default now(),
    constraint pk_family_child primary key (family_id, child_person_id),
    constraint fk_family_child_family
        foreign key (tree_id, family_id)
        references genealogy.family (tree_id, family_id),
    constraint fk_family_child_child
        foreign key (tree_id, child_person_id)
        references genealogy.person (tree_id, person_id)
);

create index ix_family_child_tree_child
    on genealogy.family_child (tree_id, child_person_id);
create index ix_family_child_child
    on genealogy.family_child (child_person_id);

-- ---------------------------------------------------------------------------
-- parent_child  [from ged.TreeParentOf — canonical parent edge]
-- ---------------------------------------------------------------------------
create table genealogy.parent_child (
    tree_id          uuid not null,
    parent_person_id uuid not null,
    child_person_id  uuid not null,
    relation_type    varchar(20) not null,        -- free text, no check
    constraint pk_parent_child
        primary key (parent_person_id, child_person_id, relation_type),
    constraint fk_parent_child_parent
        foreign key (tree_id, parent_person_id)
        references genealogy.person (tree_id, person_id),
    constraint fk_parent_child_child
        foreign key (tree_id, child_person_id)
        references genealogy.person (tree_id, person_id),
    -- A person cannot be their own parent.
    constraint ck_parent_child_distinct
        check (parent_person_id <> child_person_id)
);

create index ix_parent_child_tree_parent
    on genealogy.parent_child (tree_id, parent_person_id);
create index ix_parent_child_tree_child
    on genealogy.parent_child (tree_id, child_person_id);
create index ix_parent_child_child
    on genealogy.parent_child (child_person_id);

-- ---------------------------------------------------------------------------
-- event  [from ged.TreeEvent]
-- ---------------------------------------------------------------------------
create table genealogy.event (
    event_id           bigint generated always as identity primary key,
    tree_id            uuid not null,
    person_id          uuid not null,
    event_type         varchar(30) not null,        -- free text, no check
    event_value        text null,
    date_raw           text null,
    date_from          date null,
    date_to            date null,
    year_from          smallint null,
    year_to            smallint null,
    place_id           bigint null references genealogy.place (place_id),
    family_id          uuid null,
    related_person_id  uuid null,
    external_event_key varchar(120) null,
    is_derived         boolean not null default false,
    notes              text null,
    created_at         timestamptz not null default now(),
    constraint fk_event_person
        foreign key (tree_id, person_id)
        references genealogy.person (tree_id, person_id),
    constraint fk_event_family
        foreign key (tree_id, family_id)
        references genealogy.family (tree_id, family_id),
    constraint fk_event_related_person
        foreign key (tree_id, related_person_id)
        references genealogy.person (tree_id, person_id)
);

create index ix_event_tree_person on genealogy.event (tree_id, person_id);
create index ix_event_tree_family on genealogy.event (tree_id, family_id);
create index ix_event_place on genealogy.event (place_id);
create index ix_event_type_years
    on genealogy.event (event_type, year_from, year_to);

-- ---------------------------------------------------------------------------
-- event_citation  [from ged.TreeEventCitation]
-- ---------------------------------------------------------------------------
create table genealogy.event_citation (
    event_citation_id bigint generated always as identity primary key,
    event_id          bigint not null
        references genealogy.event (event_id) on delete cascade,
    source_origin     varchar(20) not null default 'GEDCOM'
        check (source_origin in ('GEDCOM', 'MANUAL')),
    source_ref        text null,
    source_title      text null,
    page              text null,
    quality           varchar(20) null,
    citation_date_raw text null,
    citation_text     text null,
    note              text null,
    created_at        timestamptz not null default now()
);

create index ix_event_citation_event on genealogy.event_citation (event_id);
create index ix_event_citation_source_ref on genealogy.event_citation (source_ref);
