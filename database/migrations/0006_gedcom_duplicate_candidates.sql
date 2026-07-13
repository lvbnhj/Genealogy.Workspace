-- 0006_gedcom_duplicate_candidates.sql
--
-- GEDCOM import DUPLICATE-CANDIDATE table + scoring function for the PostgreSQL
-- genealogy workspace. This stores probable duplicate person pairs discovered
-- for a staged import batch (genealogy.gedcom_import_* tables from migration
-- 0004) both WITHIN the import and BETWEEN the import and the production tree
-- (genealogy.person / person_name / place / parent_child / event /
-- event_citation from migration 0002), together with the per-facet scores and
-- human-readable evidence used to rank them.
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or extend
-- the schema by adding a new, higher-numbered migration instead of changing an
-- existing one. Editing an applied migration breaks the schema_version journal
-- and produces divergent databases.
--
-- The `genealogy` schema already exists from migration 0001_create_schemas.sql.
-- This migration only adds one table and one function inside it.
--
-- SOURCE TO PORT (SQL Server):
--   Table     : ged.GedcomImportDuplicateCandidate
--               (Database/Tables/ged/GedcomImportDuplicateCandidate.sql)
--   Procedure : ged.GenerateGedcomImportDuplicateCandidates
--               (Database/Procedures/ged/GenerateGedcomImportDuplicateCandidates.sql)
--
-- This is a FAITHFUL port. Notable mappings SQL Server -> PostgreSQL:
--   ged.GedcomImportEvent            -> genealogy.gedcom_import_event
--   ged.GedcomImportEventCitation    -> genealogy.gedcom_import_event_citation
--   ged.GedcomImportParentOf         -> genealogy.gedcom_import_parent_of
--   ged.GedcomImportPersonNameParsed -> genealogy.gedcom_import_person_name_parsed
--   ged.GedcomImportPerson           -> genealogy.gedcom_import_person
--   ged.TreeEvent                    -> genealogy.event         (person_id)
--   ged.TreeEventCitation            -> genealogy.event_citation
--   ged.TreePlace                    -> genealogy.place          (GLOBAL, no tree_id)
--   ged.TreeParentOf                 -> genealogy.parent_child   (child_person_id)
--   ged.TreePersonNames              -> genealogy.person_name    (given/surname raw)
--   ged.TreePerson                   -> genealogy.person
--   CONVERT(varchar(10), d, 23)      -> to_char(d, 'YYYY-MM-DD')  (ISO date key)
--   ISNULL(guid, all-zero-guid) eq   -> IS NOT DISTINCT FROM      (null-safe key)
--   OUTER APPLY (agg subquery)       -> LEFT JOIN LATERAL (...) ON true
--   CONCAT_WS / CONCAT               -> concat_ws / concat        (null-skipping)
--
-- FAITHFUL-PORT NO-OPS (per plan decision): the source never transitions rows
-- to 'stale' or 'accepted', and never re-evaluates "materially changed"
-- candidates. Those statuses exist only in the domain (CHECK below). We keep
-- that behaviour: the function only ever writes status = 'suggested', deletes
-- previously-suggested/stale rows, and leaves 'accepted'/'rejected' untouched.
--
-- The source procedure's optional @ReturnSummary result set (source lines
-- 431-444) is intentionally NOT ported: it is a reporting convenience for the
-- SQL Server caller. The function returns the count of inserted rows instead;
-- any summary lives in the .NET service, mirroring the 0005 preview split.

-- ---------------------------------------------------------------------------
-- gedcom_import_duplicate_candidate  [from ged.GedcomImportDuplicateCandidate]
-- ---------------------------------------------------------------------------
create table genealogy.gedcom_import_duplicate_candidate (
    duplicate_candidate_id  bigint generated always as identity primary key,
    import_batch_id         uuid not null
        references genealogy.gedcom_import_batch (import_batch_id) on delete cascade,

    candidate_scope         varchar(30) not null
        check (candidate_scope in ('within_import', 'import_vs_tree')),
    import_tree_person_id_1  uuid not null,
    import_tree_person_id_2  uuid null,
    existing_tree_person_id  uuid null,

    score                    numeric(6, 4) not null
        check (score >= 0.0 and score <= 1.0),
    name_score               numeric(6, 4) not null,
    date_score               numeric(6, 4) not null,
    place_score              numeric(6, 4) not null,
    family_score             numeric(6, 4) not null default 0,
    event_score              numeric(6, 4) not null default 0,
    negative_score           numeric(6, 4) not null,

    evidence_for             text null,
    evidence_against         text null,
    recommended_action       varchar(40) not null,
    status                   varchar(20) not null default 'suggested'
        check (status in ('suggested', 'accepted', 'rejected', 'stale')),

    created_at               timestamptz not null default now(),
    updated_at               timestamptz null,

    -- Scope / target coupling: within_import points at a second staged person;
    -- import_vs_tree points at an existing production person. (Source
    -- CK_GedcomImportDuplicateCandidate_Target.)
    constraint ck_gedcom_import_duplicate_candidate_target check (
        (candidate_scope = 'within_import'
            and import_tree_person_id_2 is not null
            and existing_tree_person_id is null)
        or
        (candidate_scope = 'import_vs_tree'
            and import_tree_person_id_2 is null
            and existing_tree_person_id is not null)
    )
);

create index ix_gedcom_import_duplicate_candidate_batch_score
    on genealogy.gedcom_import_duplicate_candidate (import_batch_id, status, score desc);
create index ix_gedcom_import_duplicate_candidate_import_person_1
    on genealogy.gedcom_import_duplicate_candidate (import_batch_id, import_tree_person_id_1);
create index ix_gedcom_import_duplicate_candidate_import_person_2
    on genealogy.gedcom_import_duplicate_candidate (import_batch_id, import_tree_person_id_2);
create index ix_gedcom_import_duplicate_candidate_existing_person
    on genealogy.gedcom_import_duplicate_candidate (import_batch_id, existing_tree_person_id);

comment on table genealogy.gedcom_import_duplicate_candidate is
    'Faithful port of ged.GedcomImportDuplicateCandidate. Probable duplicate '
    'person pairs for a staged import batch, both within the import '
    '(within_import) and against the production tree (import_vs_tree).';

-- ---------------------------------------------------------------------------
-- genealogy.generate_gedcom_import_duplicate_candidates(p_import_batch_id, p_min_score)
--   Faithful port of ged.GenerateGedcomImportDuplicateCandidates. Regenerates
--   (idempotently) the 'suggested' candidate rows for a batch and returns the
--   number of rows inserted. VOLATILE by nature (it DELETEs + INSERTs).
-- ---------------------------------------------------------------------------
create function genealogy.generate_gedcom_import_duplicate_candidates(
    p_import_batch_id uuid,
    p_min_score numeric default 0.7500
)
returns integer
language plpgsql
as $$
declare
    v_tree_id  uuid;
    v_inserted integer;
begin
    -- Resolve the batch's tree; raise if the batch does not exist.
    -- (Source lines 9-15.)
    select b.tree_id
    into v_tree_id
    from genealogy.gedcom_import_batch b
    where b.import_batch_id = p_import_batch_id;

    if v_tree_id is null then
        raise exception 'GEDCOM import batch not found.'
            using errcode = 'no_data_found';
    end if;

    -- Clear only regenerable rows; never touch 'accepted'/'rejected'.
    -- (Source lines 17-19.)
    delete from genealogy.gedcom_import_duplicate_candidate
    where import_batch_id = p_import_batch_id
      and status in ('suggested', 'stale');

    with
    -- Staged "life": birth year / death year / birth place per staged person,
    -- from BIRT/CHR/DEAT events. (Source lines 21-31, StagedLife.)
    staged_life as (
        select
            e.tree_person_id,
            min(case when e.event_type in ('BIRT', 'CHR')
                     then coalesce(e.year_from, e.year_to) end) as birth_year,
            min(case when e.event_type = 'DEAT'
                     then coalesce(e.year_from, e.year_to) end) as death_year,
            min(case when e.event_type in ('BIRT', 'CHR')
                     then e.place_normalized end) as birth_place
        from genealogy.gedcom_import_event e
        where e.import_batch_id = p_import_batch_id
        group by e.tree_person_id
    ),
    -- Staged parent coverage: DISTINCT parent count per child.
    -- (Source lines 32-40, StagedParentCoverage.)
    staged_parent_coverage as (
        select
            po.child_tree_person_id as tree_person_id,
            count(distinct po.parent_tree_person_id) as parent_count
        from genealogy.gedcom_import_parent_of po
        where po.import_batch_id = p_import_batch_id
        group by po.child_tree_person_id
    ),
    -- Staged sourced life events: non-derived BIRT/CHR/DEAT with a non-blank
    -- date_raw AND at least one citation. Keyed by a date-range key (from
    -- date_from/date_to) or the lowercased/trimmed raw date.
    -- (Source lines 41-68, StagedSourcedLifeEvent.)
    staged_sourced_life_event as (
        select
            e.tree_person_id,
            e.event_type,
            case
                when e.date_from is not null or e.date_to is not null
                    then coalesce(to_char(e.date_from, 'YYYY-MM-DD'), '')
                         || '|' || coalesce(to_char(e.date_to, 'YYYY-MM-DD'), '')
            end as date_range_key,
            lower(btrim(e.date_raw)) as date_raw_key,
            min(e.date_raw) as date_raw
        from genealogy.gedcom_import_event e
        where e.import_batch_id = p_import_batch_id
          and e.is_derived = false
          and e.event_type in ('BIRT', 'CHR', 'DEAT')
          and nullif(btrim(e.date_raw), '') is not null
          and exists (
              select 1
              from genealogy.gedcom_import_event_citation c
              where c.import_batch_id = e.import_batch_id
                and c.event_row_number = e.row_number
          )
        group by
            e.tree_person_id,
            e.event_type,
            case
                when e.date_from is not null or e.date_to is not null
                    then coalesce(to_char(e.date_from, 'YYYY-MM-DD'), '')
                         || '|' || coalesce(to_char(e.date_to, 'YYYY-MM-DD'), '')
            end,
            lower(btrim(e.date_raw))
    ),
    -- Staged names in scope: parser OK, confidence >= 0.75, non-blank
    -- normalized full name, enriched with life + parent-coverage columns.
    -- (Source lines 69-95, StagedName.) Uses the PARSED given/surname columns.
    staged_name as (
        select distinct
            p.tree_person_id,
            ip.sex,
            p.raw_name,
            p.given_name_normalized,
            p.surname_normalized,
            p.full_name_normalized,
            l.birth_year,
            l.death_year,
            l.birth_place,
            coalesce(pc.parent_count, 0) as parent_count
        from genealogy.gedcom_import_person_name_parsed p
        join genealogy.gedcom_import_person ip
          on ip.import_batch_id = p.import_batch_id
         and ip.tree_person_id = p.tree_person_id
        left join staged_life l
          on l.tree_person_id = p.tree_person_id
        left join staged_parent_coverage pc
          on pc.tree_person_id = p.tree_person_id
        where p.import_batch_id = p_import_batch_id
          and p.parser_status = 'OK'
          and p.normalization_confidence >= 0.7500
          and p.full_name_normalized is not null
          and p.full_name_normalized <> ''
    ),
    -- within_import raw candidate pairs (staged vs staged, id1 < id2).
    -- (Source lines 96-182, WithinRaw.)
    within_raw as (
        select
            'within_import'::varchar(30) as candidate_scope,
            least(n1.tree_person_id, n2.tree_person_id) as import_tree_person_id_1,
            greatest(n1.tree_person_id, n2.tree_person_id) as import_tree_person_id_2,
            null::uuid as existing_tree_person_id,
            (case
                when n1.full_name_normalized = n2.full_name_normalized then 1.0000
                else 0.8200
            end)::numeric(6, 4) as name_score,
            (case
                when n1.birth_year is null or n2.birth_year is null then 0.5000
                when abs(n1.birth_year - n2.birth_year) <= 1 then 1.0000
                when abs(n1.birth_year - n2.birth_year) <= 5 then 0.7500
                else 0.0000
            end)::numeric(6, 4) as date_score,
            (case
                when n1.birth_place is null or n2.birth_place is null then 0.5000
                when n1.birth_place = n2.birth_place then 1.0000
                else 0.2500
            end)::numeric(6, 4) as place_score,
            0.0000::numeric(6, 4) as family_score,
            (case when sourced.has_sourced_date_match = 1 then 1.0000 else 0.0000 end)::numeric(6, 4) as event_score,
            (
                (case when n1.sex is not null and n2.sex is not null and n1.sex <> n2.sex then 0.3500 else 0.0000 end)
              + (case when n1.birth_year is not null and n2.birth_year is not null and abs(n1.birth_year - n2.birth_year) > 10 then 0.3500 else 0.0000 end)
              + (case when sourced.has_sourced_date_conflict = 1 then 1.0000 else 0.0000 end)
            )::numeric(6, 4) as negative_score,
            concat_ws('; ',
                case when n1.full_name_normalized = n2.full_name_normalized
                     then concat('exact normalized full name: ', n1.full_name_normalized) end,
                case when n1.full_name_normalized <> n2.full_name_normalized
                     then concat('same normalized given/surname: ', n1.given_name_normalized, ' ', n1.surname_normalized) end,
                case when n1.birth_year is not null and n2.birth_year is not null and abs(n1.birth_year - n2.birth_year) <= 5
                     then concat('birth years compatible: ', n1.birth_year, ' vs ', n2.birth_year) end,
                case when n1.birth_place is not null and n1.birth_place = n2.birth_place
                     then concat('birth place matches: ', n1.birth_place) end,
                case when n1.parent_count = 0 then 'left person has no staged parents' end,
                case when n2.parent_count = 0 then 'right person has no staged parents' end,
                sourced.sourced_date_match_evidence
            ) as evidence_for,
            concat_ws('; ',
                case when n1.sex is not null and n2.sex is not null and n1.sex <> n2.sex
                     then concat('sex differs: ', n1.sex, ' vs ', n2.sex) end,
                case when n1.birth_year is not null and n2.birth_year is not null and abs(n1.birth_year - n2.birth_year) > 10
                     then concat('birth years differ: ', n1.birth_year, ' vs ', n2.birth_year) end,
                case when n1.birth_place is not null and n2.birth_place is not null and n1.birth_place <> n2.birth_place
                     then concat('birth places differ: ', n1.birth_place, ' vs ', n2.birth_place) end,
                sourced.sourced_date_conflict_evidence
            ) as evidence_against
        from staged_name n1
        join staged_name n2
          on n1.tree_person_id < n2.tree_person_id
         and (
                n1.full_name_normalized = n2.full_name_normalized
             or (
                    n1.given_name_normalized is not null
                and n1.given_name_normalized <> ''
                and n1.surname_normalized is not null
                and n1.surname_normalized <> ''
                and n1.given_name_normalized = n2.given_name_normalized
                and n1.surname_normalized = n2.surname_normalized
                )
         )
         and (n1.parent_count = 0 or n2.parent_count = 0)
        left join lateral (
            select
                max(case
                    when e1.date_range_key is not null and e2.date_range_key is not null and e1.date_range_key <> e2.date_range_key then 1
                    when e1.date_range_key is null and e2.date_range_key is null and e1.date_raw_key <> e2.date_raw_key then 1
                    else 0
                end) as has_sourced_date_conflict,
                max(case
                    when e1.date_range_key is not null and e2.date_range_key is not null and e1.date_range_key = e2.date_range_key then 1
                    when e1.date_raw_key = e2.date_raw_key then 1
                    else 0
                end) as has_sourced_date_match,
                min(case
                    when e1.date_range_key is not null and e2.date_range_key is not null and e1.date_range_key <> e2.date_range_key
                        then concat('source-backed ', e1.event_type, ' dates differ: ', e1.date_raw, ' vs ', e2.date_raw)
                    when e1.date_range_key is null and e2.date_range_key is null and e1.date_raw_key <> e2.date_raw_key
                        then concat('source-backed ', e1.event_type, ' dates differ: ', e1.date_raw, ' vs ', e2.date_raw)
                end) as sourced_date_conflict_evidence,
                min(case
                    when e1.date_range_key is not null and e2.date_range_key is not null and e1.date_range_key = e2.date_range_key
                        then concat('source-backed ', e1.event_type, ' date range matches: ', e1.date_raw)
                    when e1.date_raw_key = e2.date_raw_key
                        then concat('source-backed ', e1.event_type, ' date matches exactly: ', e1.date_raw)
                end) as sourced_date_match_evidence
            from staged_sourced_life_event e1
            join staged_sourced_life_event e2
              on e2.tree_person_id = n2.tree_person_id
             and e2.event_type = e1.event_type
            where e1.tree_person_id = n1.tree_person_id
        ) sourced on true
    ),
    -- Existing (production) "life" per person. (Source lines 183-194, ExistingLife.)
    existing_life as (
        select
            e.person_id as tree_person_id,
            min(case when e.event_type in ('BIRT', 'CHR')
                     then coalesce(e.year_from, e.year_to) end) as birth_year,
            min(case when e.event_type = 'DEAT'
                     then coalesce(e.year_from, e.year_to) end) as death_year,
            min(case when e.event_type in ('BIRT', 'CHR')
                     then pl.place_normalized end) as birth_place
        from genealogy.event e
        left join genealogy.place pl on pl.place_id = e.place_id
        where e.tree_id = v_tree_id
        group by e.person_id
    ),
    -- Existing parent coverage. (Source lines 195-203, ExistingParentCoverage.)
    existing_parent_coverage as (
        select
            pc.child_person_id as tree_person_id,
            count(distinct pc.parent_person_id) as parent_count
        from genealogy.parent_child pc
        where pc.tree_id = v_tree_id
        group by pc.child_person_id
    ),
    -- Existing sourced life events. (Source lines 204-230, ExistingSourcedLifeEvent.)
    existing_sourced_life_event as (
        select
            e.person_id as tree_person_id,
            e.event_type,
            case
                when e.date_from is not null or e.date_to is not null
                    then coalesce(to_char(e.date_from, 'YYYY-MM-DD'), '')
                         || '|' || coalesce(to_char(e.date_to, 'YYYY-MM-DD'), '')
            end as date_range_key,
            lower(btrim(e.date_raw)) as date_raw_key,
            min(e.date_raw) as date_raw
        from genealogy.event e
        where e.tree_id = v_tree_id
          and e.is_derived = false
          and e.event_type in ('BIRT', 'CHR', 'DEAT')
          and nullif(btrim(e.date_raw), '') is not null
          and exists (
              select 1
              from genealogy.event_citation c
              where c.event_id = e.event_id
          )
        group by
            e.person_id,
            e.event_type,
            case
                when e.date_from is not null or e.date_to is not null
                    then coalesce(to_char(e.date_from, 'YYYY-MM-DD'), '')
                         || '|' || coalesce(to_char(e.date_to, 'YYYY-MM-DD'), '')
            end,
            lower(btrim(e.date_raw))
    ),
    -- Existing names: uses production person_name.given/surname as the
    -- given/surname keys and person.sex. (Source lines 231-253, ExistingName.)
    existing_name as (
        select distinct
            pn.person_id as tree_person_id,
            p.sex,
            pn.full_name as raw_name,
            pn.given as given_name_normalized,
            pn.surname as surname_normalized,
            pn.full_name_normalized,
            l.birth_year,
            l.death_year,
            l.birth_place,
            coalesce(pc.parent_count, 0) as parent_count
        from genealogy.person_name pn
        join genealogy.person p
          on p.tree_id = pn.tree_id
         and p.person_id = pn.person_id
        left join existing_life l
          on l.tree_person_id = pn.person_id
        left join existing_parent_coverage pc
          on pc.tree_person_id = pn.person_id
        where pn.tree_id = v_tree_id
    ),
    -- import_vs_tree raw candidate pairs (staged vs existing production).
    -- (Source lines 254-340, ImportVsTreeRaw.)
    import_vs_tree_raw as (
        select
            'import_vs_tree'::varchar(30) as candidate_scope,
            n.tree_person_id as import_tree_person_id_1,
            null::uuid as import_tree_person_id_2,
            e.tree_person_id as existing_tree_person_id,
            (case
                when n.full_name_normalized = e.full_name_normalized then 1.0000
                else 0.8200
            end)::numeric(6, 4) as name_score,
            (case
                when n.birth_year is null or e.birth_year is null then 0.5000
                when abs(n.birth_year - e.birth_year) <= 1 then 1.0000
                when abs(n.birth_year - e.birth_year) <= 5 then 0.7500
                else 0.0000
            end)::numeric(6, 4) as date_score,
            (case
                when n.birth_place is null or e.birth_place is null then 0.5000
                when n.birth_place = e.birth_place then 1.0000
                else 0.2500
            end)::numeric(6, 4) as place_score,
            0.0000::numeric(6, 4) as family_score,
            (case when sourced.has_sourced_date_match = 1 then 1.0000 else 0.0000 end)::numeric(6, 4) as event_score,
            (
                (case when n.sex is not null and e.sex is not null and n.sex <> e.sex then 0.3500 else 0.0000 end)
              + (case when n.birth_year is not null and e.birth_year is not null and abs(n.birth_year - e.birth_year) > 10 then 0.3500 else 0.0000 end)
              + (case when sourced.has_sourced_date_conflict = 1 then 1.0000 else 0.0000 end)
            )::numeric(6, 4) as negative_score,
            concat_ws('; ',
                case when n.full_name_normalized = e.full_name_normalized
                     then concat('exact normalized full name: ', n.full_name_normalized) end,
                case when n.full_name_normalized <> e.full_name_normalized
                     then concat('same given/surname: ', n.given_name_normalized, ' ', n.surname_normalized) end,
                case when n.birth_year is not null and e.birth_year is not null and abs(n.birth_year - e.birth_year) <= 5
                     then concat('birth years compatible: ', n.birth_year, ' vs ', e.birth_year) end,
                case when n.birth_place is not null and n.birth_place = e.birth_place
                     then concat('birth place matches: ', n.birth_place) end,
                case when n.parent_count = 0 then 'staged person has no staged parents' end,
                case when e.parent_count = 0 then 'existing person has no tree parents' end,
                sourced.sourced_date_match_evidence
            ) as evidence_for,
            concat_ws('; ',
                case when n.sex is not null and e.sex is not null and n.sex <> e.sex
                     then concat('sex differs: ', n.sex, ' vs ', e.sex) end,
                case when n.birth_year is not null and e.birth_year is not null and abs(n.birth_year - e.birth_year) > 10
                     then concat('birth years differ: ', n.birth_year, ' vs ', e.birth_year) end,
                case when n.birth_place is not null and e.birth_place is not null and n.birth_place <> e.birth_place
                     then concat('birth places differ: ', n.birth_place, ' vs ', e.birth_place) end,
                sourced.sourced_date_conflict_evidence
            ) as evidence_against
        from staged_name n
        join existing_name e
          on n.tree_person_id <> e.tree_person_id
         and (
                n.full_name_normalized = e.full_name_normalized
             or (
                    n.given_name_normalized is not null
                and n.given_name_normalized <> ''
                and n.surname_normalized is not null
                and n.surname_normalized <> ''
                and n.given_name_normalized = e.given_name_normalized
                and n.surname_normalized = e.surname_normalized
                )
         )
         and (n.parent_count = 0 or e.parent_count = 0)
        left join lateral (
            select
                max(case
                    when se.date_range_key is not null and ee.date_range_key is not null and se.date_range_key <> ee.date_range_key then 1
                    when se.date_range_key is null and ee.date_range_key is null and se.date_raw_key <> ee.date_raw_key then 1
                    else 0
                end) as has_sourced_date_conflict,
                max(case
                    when se.date_range_key is not null and ee.date_range_key is not null and se.date_range_key = ee.date_range_key then 1
                    when se.date_raw_key = ee.date_raw_key then 1
                    else 0
                end) as has_sourced_date_match,
                min(case
                    when se.date_range_key is not null and ee.date_range_key is not null and se.date_range_key <> ee.date_range_key
                        then concat('source-backed ', se.event_type, ' dates differ: ', se.date_raw, ' vs ', ee.date_raw)
                    when se.date_range_key is null and ee.date_range_key is null and se.date_raw_key <> ee.date_raw_key
                        then concat('source-backed ', se.event_type, ' dates differ: ', se.date_raw, ' vs ', ee.date_raw)
                end) as sourced_date_conflict_evidence,
                min(case
                    when se.date_range_key is not null and ee.date_range_key is not null and se.date_range_key = ee.date_range_key
                        then concat('source-backed ', se.event_type, ' date range matches: ', se.date_raw)
                    when se.date_raw_key = ee.date_raw_key
                        then concat('source-backed ', se.event_type, ' date matches exactly: ', se.date_raw)
                end) as sourced_date_match_evidence
            from staged_sourced_life_event se
            join existing_sourced_life_event ee
              on ee.tree_person_id = e.tree_person_id
             and ee.event_type = se.event_type
            where se.tree_person_id = n.tree_person_id
        ) sourced on true
    ),
    -- Composite score. NOTE: family_score/event_score are carried through but
    -- are NOT part of the composite (source lines 341-384, Scored).
    scored as (
        select
            candidate_scope,
            import_tree_person_id_1,
            import_tree_person_id_2,
            existing_tree_person_id,
            greatest(0, least(1,
                name_score * 0.7500 + date_score * 0.1500 + place_score * 0.1000 - negative_score
            ))::numeric(6, 4) as score,
            name_score,
            date_score,
            place_score,
            family_score,
            event_score,
            negative_score,
            evidence_for,
            evidence_against
        from within_raw
        union all
        select
            candidate_scope,
            import_tree_person_id_1,
            import_tree_person_id_2,
            existing_tree_person_id,
            greatest(0, least(1,
                name_score * 0.7500 + date_score * 0.1500 + place_score * 0.1000 - negative_score
            ))::numeric(6, 4) as score,
            name_score,
            date_score,
            place_score,
            family_score,
            event_score,
            negative_score,
            evidence_for,
            evidence_against
        from import_vs_tree_raw
    ),
    -- Rank duplicates of the same identity, keep the best. Only rows at or
    -- above the minimum score are candidates. (Source lines 385-395, Ranked.)
    ranked as (
        select
            s.*,
            row_number() over (
                partition by s.candidate_scope, s.import_tree_person_id_1,
                             s.import_tree_person_id_2, s.existing_tree_person_id
                order by s.score desc, s.name_score desc, s.date_score desc, s.place_score desc
            ) as rn
        from scored s
        where s.score >= p_min_score
    )
    insert into genealogy.gedcom_import_duplicate_candidate
        (import_batch_id, candidate_scope, import_tree_person_id_1, import_tree_person_id_2,
         existing_tree_person_id, score, name_score, date_score, place_score, family_score,
         event_score, negative_score, evidence_for, evidence_against, recommended_action, status)
    select
        p_import_batch_id,
        r.candidate_scope,
        r.import_tree_person_id_1,
        r.import_tree_person_id_2,
        r.existing_tree_person_id,
        r.score,
        r.name_score,
        r.date_score,
        r.place_score,
        r.family_score,
        r.event_score,
        r.negative_score,
        nullif(r.evidence_for, ''),
        nullif(r.evidence_against, ''),
        case when r.score >= 0.9000 then 'review_high_confidence' else 'review' end,
        'suggested'
    from ranked r
    where r.rn = 1
      -- Suppress any identity a user previously rejected. (Source lines 419-428,
      -- with IS NOT DISTINCT FROM replacing the ISNULL(zero-guid) comparisons.)
      and not exists (
          select 1
          from genealogy.gedcom_import_duplicate_candidate old
          where old.import_batch_id = p_import_batch_id
            and old.candidate_scope = r.candidate_scope
            and old.import_tree_person_id_1 = r.import_tree_person_id_1
            and old.import_tree_person_id_2 is not distinct from r.import_tree_person_id_2
            and old.existing_tree_person_id is not distinct from r.existing_tree_person_id
            and old.status = 'rejected'
      );

    get diagnostics v_inserted = row_count;
    return v_inserted;
end;
$$;

comment on function genealogy.generate_gedcom_import_duplicate_candidates(uuid, numeric) is
    'Faithful port of ged.GenerateGedcomImportDuplicateCandidates. Regenerates '
    'the ''suggested'' duplicate candidates for a staged batch (within_import + '
    'import_vs_tree) and returns the count inserted. Deletes prior '
    'suggested/stale rows, suppresses identities already ''rejected'', and never '
    'modifies ''accepted''/''rejected'' rows. No ''stale''/''accepted'' transition '
    'is performed (faithful to the source).';
