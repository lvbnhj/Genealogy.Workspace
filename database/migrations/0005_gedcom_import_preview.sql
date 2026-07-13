-- 0005_gedcom_import_preview.sql
--
-- GEDCOM import PREVIEW change-classification functions for the PostgreSQL
-- genealogy workspace. These functions diff a staged import batch
-- (genealogy.gedcom_import_* tables from migration 0004) against the production
-- tree (genealogy.person / person_name / place / family / family_child /
-- parent_child / event / event_citation from migration 0002) and return the
-- per-entity ADD / UPDATE / MISSING_FROM_IMPORT / REPLACE counts plus a capped
-- sample of person-level changes.
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or extend
-- the schema by adding a new, higher-numbered migration instead of changing an
-- existing one. Editing an applied migration breaks the schema_version journal
-- and produces divergent databases.
--
-- SOURCE TO PORT (SQL Server): ged.GetGedcomImportPreview
--   (Database/Procedures/ged/GetGedcomImportPreview.sql, 393 lines). This is a
--   FAITHFUL port of that procedure's second and third result sets. The first
--   result set (batch header) and the STAGED -> PREVIEWED status flip stay in
--   the .NET service (GedcomImportPreviewService) so these functions remain
--   side-effect-free (STABLE).
--
-- Both functions are STABLE (read-only): they never modify data.
--
-- DELIBERATE DEVIATION forced by the Phase-2 production schema: the source
-- proc's SpouseOf branch (source lines 176-185) diffs staged spouse edges
-- against ged.TreeSpouseOf, a directional spouse-edge table that DOES NOT EXIST
-- in production. Production models spouses only as the unordered pair
-- (spouse1_person_id, spouse2_person_id) on genealogy.family (see migration
-- 0002 and FamilyContextRepository). So SpouseOf ADD here is computed as
-- "staged spouse edge whose UNORDERED pair {from, to} has no matching family
-- spouse pair in the same tree." This necessarily drops the source's
-- RelationType and FamilyId equality checks (the derived model carries neither
-- on a per-edge basis) and collapses From/To direction into an unordered pair.

-- ---------------------------------------------------------------------------
-- genealogy.gedcom_import_preview_counts(p_import_batch_id uuid)
--   Returns one row per (entity_type, change_type) with a positive row_count,
--   ordered by entity_type, change_type -- mirroring source result set 2
--   (source lines 41-368), including the final `WHERE [RowCount] > 0
--   ORDER BY EntityType, ChangeType`.
-- ---------------------------------------------------------------------------
create function genealogy.gedcom_import_preview_counts(p_import_batch_id uuid)
returns table (entity_type text, change_type text, row_count bigint)
language plpgsql
stable
as $$
declare
    v_tree_id uuid;
    -- Sentinel used to reproduce the source proc's ISNULL(guid, all-zero-guid)
    -- comparisons (e.g. source lines 138-139, 248-249).
    v_zero_uuid constant uuid := '00000000-0000-0000-0000-000000000000';
begin
    select b.tree_id
    into v_tree_id
    from genealogy.gedcom_import_batch b
    where b.import_batch_id = p_import_batch_id;

    if v_tree_id is null then
        raise exception 'GEDCOM import batch not found.'
            using errcode = 'no_data_found';
    end if;

    return query
    select x.entity_type, x.change_type, x.row_count
    from (
        -- Person ADD  (source lines 43-47)
        select 'Person'::text as entity_type, 'ADD'::text as change_type, count(*)::bigint as row_count
        from genealogy.gedcom_import_person s
        left join genealogy.person t
            on t.tree_id = v_tree_id and t.person_id = s.tree_person_id
        where s.import_batch_id = p_import_batch_id
          and t.person_id is null

        -- Person UPDATE  (source lines 50-60)
        union all
        select 'Person', 'UPDATE', count(*)::bigint
        from genealogy.gedcom_import_person s
        join genealogy.person t
            on t.tree_id = v_tree_id and t.person_id = s.tree_person_id
        where s.import_batch_id = p_import_batch_id
          and (
                coalesce(t.external_id, '') <> coalesce(s.external_id, '')
             or coalesce(t.sex::text, '') <> coalesce(s.sex::text, '')
             or coalesce(t.is_living, false) <> coalesce(s.is_living, false)
             or coalesce(t.primary_display_name, '') <> coalesce(s.primary_display_name, '')
             or coalesce(t.surname_normalized, '') <> coalesce(s.surname_normalized, '')
          )

        -- Person MISSING_FROM_IMPORT  (source lines 63-69)
        union all
        select 'Person', 'MISSING_FROM_IMPORT', count(*)::bigint
        from genealogy.person t
        left join genealogy.gedcom_import_person s
            on s.import_batch_id = p_import_batch_id and s.tree_person_id = t.person_id
        where t.tree_id = v_tree_id
          and s.tree_person_id is null

        -- PersonName ADD  (source lines 72-80)
        union all
        select 'PersonName', 'ADD', count(*)::bigint
        from genealogy.gedcom_import_person_name s
        left join genealogy.person_name t
            on t.tree_id = v_tree_id
           and t.person_id = s.tree_person_id
           and t.script_code = s.script_code
           and t.full_name_normalized = s.full_name_normalized
        where s.import_batch_id = p_import_batch_id
          and t.person_name_id is null

        -- PersonName MISSING_FROM_IMPORT  (source lines 83-91)
        union all
        select 'PersonName', 'MISSING_FROM_IMPORT', count(*)::bigint
        from genealogy.person_name t
        left join genealogy.gedcom_import_person_name s
            on s.import_batch_id = p_import_batch_id
           and s.tree_person_id = t.person_id
           and s.script_code = t.script_code
           and s.full_name_normalized = t.full_name_normalized
        where t.tree_id = v_tree_id
          and s.row_number is null

        -- Place ADD  (source lines 94-98). Place is GLOBAL (no tree_id).
        union all
        select 'Place', 'ADD', count(*)::bigint
        from genealogy.gedcom_import_place s
        left join genealogy.place t on t.place_raw = s.place_raw
        where s.import_batch_id = p_import_batch_id
          and t.place_id is null

        -- ParentOf ADD  (source lines 101-109)
        union all
        select 'ParentOf', 'ADD', count(*)::bigint
        from genealogy.gedcom_import_parent_of s
        left join genealogy.parent_child t
            on t.tree_id = v_tree_id
           and t.parent_person_id = s.parent_tree_person_id
           and t.child_person_id = s.child_tree_person_id
           and t.relation_type = s.relation_type
        where s.import_batch_id = p_import_batch_id
          and t.parent_person_id is null

        -- ParentOf MISSING_FROM_IMPORT  (source lines 112-120)
        union all
        select 'ParentOf', 'MISSING_FROM_IMPORT', count(*)::bigint
        from genealogy.parent_child t
        left join genealogy.gedcom_import_parent_of s
            on s.import_batch_id = p_import_batch_id
           and s.parent_tree_person_id = t.parent_person_id
           and s.child_tree_person_id = t.child_person_id
           and s.relation_type = t.relation_type
        where t.tree_id = v_tree_id
          and s.parent_tree_person_id is null

        -- Family ADD  (source lines 123-129)
        union all
        select 'Family', 'ADD', count(*)::bigint
        from genealogy.gedcom_import_family s
        left join genealogy.family t
            on t.tree_id = v_tree_id and t.family_id = s.family_id
        where s.import_batch_id = p_import_batch_id
          and s.spouse1_tree_person_id is not null
          and s.spouse2_tree_person_id is not null
          and t.family_id is null

        -- Family UPDATE  (source lines 132-144)
        union all
        select 'Family', 'UPDATE', count(*)::bigint
        from genealogy.gedcom_import_family s
        join genealogy.family t
            on t.tree_id = v_tree_id and t.family_id = s.family_id
        left join genealogy.place p on p.place_raw = s.marriage_place_raw
        where s.import_batch_id = p_import_batch_id
          and (
                coalesce(t.spouse1_person_id, v_zero_uuid) <> coalesce(s.spouse1_tree_person_id, v_zero_uuid)
             or coalesce(t.spouse2_person_id, v_zero_uuid) <> coalesce(s.spouse2_tree_person_id, v_zero_uuid)
             or coalesce(t.marriage_date_raw, '') <> coalesce(s.marriage_date_raw, '')
             or coalesce(t.marriage_year, (-1)::smallint) <> coalesce(s.marriage_year, (-1)::smallint)
             or coalesce(t.marriage_place_id, (-1)::bigint) <> coalesce(p.place_id, (-1)::bigint)
             or coalesce(t.notes, '') <> coalesce(s.notes, '')
          )

        -- Family MISSING_FROM_IMPORT  (source lines 147-153)
        union all
        select 'Family', 'MISSING_FROM_IMPORT', count(*)::bigint
        from genealogy.family t
        left join genealogy.gedcom_import_family s
            on s.import_batch_id = p_import_batch_id and s.family_id = t.family_id
        where t.tree_id = v_tree_id
          and s.family_id is null

        -- FamilyChild ADD  (source lines 156-163)
        union all
        select 'FamilyChild', 'ADD', count(*)::bigint
        from genealogy.gedcom_import_family_child s
        left join genealogy.family_child t
            on t.tree_id = v_tree_id
           and t.family_id = s.family_id
           and t.child_person_id = s.child_tree_person_id
        where s.import_batch_id = p_import_batch_id
          and t.family_id is null

        -- FamilyChild MISSING_FROM_IMPORT  (source lines 166-173)
        union all
        select 'FamilyChild', 'MISSING_FROM_IMPORT', count(*)::bigint
        from genealogy.family_child t
        left join genealogy.gedcom_import_family_child s
            on s.import_batch_id = p_import_batch_id
           and s.family_id = t.family_id
           and s.child_tree_person_id = t.child_person_id
        where t.tree_id = v_tree_id
          and s.family_id is null

        -- SpouseOf ADD  (source lines 176-185).
        -- DERIVED-SPOUSE DEVIATION (see file header): production has no
        -- ged.TreeSpouseOf equivalent. An ADD is a staged spouse edge whose
        -- unordered pair {from, to} matches no genealogy.family spouse pair in
        -- the same tree. RelationType/FamilyId from the source match cannot be
        -- reproduced against the derived model.
        union all
        select 'SpouseOf', 'ADD', count(*)::bigint
        from genealogy.gedcom_import_spouse_of s
        where s.import_batch_id = p_import_batch_id
          and not exists (
              select 1
              from genealogy.family f
              where f.tree_id = v_tree_id
                and least(f.spouse1_person_id, f.spouse2_person_id)
                    = least(s.from_tree_person_id, s.to_tree_person_id)
                and greatest(f.spouse1_person_id, f.spouse2_person_id)
                    = greatest(s.from_tree_person_id, s.to_tree_person_id)
          )

        -- Event UPDATE  (source lines 188-229). Reproduces the three
        -- special-cased match shapes: (1) staged DERIVED MARR matching an
        -- existing EXPLICIT (non-derived, no family) MARR; (2) staged CHR
        -- matching an existing non-derived BIRT (christening promoted to
        -- birth); (3) staged DEAT matching a DEAT with the same derived flag
        -- but a DIFFERENT place (place-mismatch => update).
        union all
        select 'Event', 'UPDATE', count(*)::bigint
        from genealogy.gedcom_import_event s
        left join genealogy.place p on p.place_raw = s.place_raw
        join genealogy.event t
            on t.tree_id = v_tree_id
           and t.person_id = s.tree_person_id
           and t.external_event_key is null
           and (
                  (   -- (1) MARR derived-vs-explicit  (source lines 196-207)
                      s.event_type = 'MARR' and s.is_derived = true
                  and t.event_type = 'MARR' and t.is_derived = false and t.family_id is null
                  and coalesce(t.event_value, '') = coalesce(s.event_value, '')
                  and coalesce(t.date_raw, '') = coalesce(s.date_raw, '')
                  and coalesce(t.year_from, (-32768)::smallint) = coalesce(s.year_from, (-32768)::smallint)
                  and coalesce(t.year_to, (-32768)::smallint) = coalesce(s.year_to, (-32768)::smallint)
                  and coalesce(t.place_id, (-1)::bigint) = coalesce(p.place_id, (-1)::bigint)
                  )
               or (   -- (2) CHR -> BIRT  (source lines 208-217)
                      s.event_type = 'CHR'
                  and t.event_type = 'BIRT' and t.is_derived = false
                  and coalesce(t.event_value, '') = coalesce(s.event_value, '')
                  and coalesce(t.date_raw, '') = coalesce(s.date_raw, '')
                  and coalesce(t.year_from, (-32768)::smallint) = coalesce(s.year_from, (-32768)::smallint)
                  and coalesce(t.year_to, (-32768)::smallint) = coalesce(s.year_to, (-32768)::smallint)
                  and coalesce(t.place_id, (-1)::bigint) = coalesce(p.place_id, (-1)::bigint)
                  )
               or (   -- (3) DEAT with place MISMATCH  (source lines 218-227)
                      s.event_type = 'DEAT'
                  and t.event_type = 'DEAT' and t.is_derived = s.is_derived
                  and coalesce(t.event_value, '') = coalesce(s.event_value, '')
                  and coalesce(t.date_raw, '') = coalesce(s.date_raw, '')
                  and coalesce(t.year_from, (-32768)::smallint) = coalesce(s.year_from, (-32768)::smallint)
                  and coalesce(t.year_to, (-32768)::smallint) = coalesce(s.year_to, (-32768)::smallint)
                  and coalesce(t.place_id, (-1)::bigint) <> coalesce(p.place_id, (-1)::bigint)
                  )
           )
        where s.import_batch_id = p_import_batch_id

        -- Event ADD  (source lines 232-288). A staged event is ADD when no
        -- production event matches it by external key, by full attribute
        -- equality, or by any of the MARR/CHR/DEAT special shapes below. Note
        -- the ADD DEAT shape (source lines 276-285) has NO place check (unlike
        -- the UPDATE DEAT shape, which requires a place mismatch).
        union all
        select 'Event', 'ADD', count(*)::bigint
        from genealogy.gedcom_import_event s
        left join genealogy.place p on p.place_raw = s.place_raw
        left join genealogy.event t
            on t.tree_id = v_tree_id
           and t.person_id = s.tree_person_id
           and (
                  t.external_event_key = s.external_event_key      -- source line 239
               or (   -- full attribute match on key-less events  (source lines 240-251)
                      t.external_event_key is null
                  and t.event_type = s.event_type
                  and coalesce(t.event_value, '') = coalesce(s.event_value, '')
                  and coalesce(t.date_raw, '') = coalesce(s.date_raw, '')
                  and coalesce(t.year_from, (-32768)::smallint) = coalesce(s.year_from, (-32768)::smallint)
                  and coalesce(t.year_to, (-32768)::smallint) = coalesce(s.year_to, (-32768)::smallint)
                  and coalesce(t.place_id, (-1)::bigint) = coalesce(p.place_id, (-1)::bigint)
                  and coalesce(t.family_id, v_zero_uuid) = coalesce(s.family_id, v_zero_uuid)
                  and coalesce(t.related_person_id, v_zero_uuid) = coalesce(s.related_tree_person_id, v_zero_uuid)
                  and t.is_derived = s.is_derived
                  )
               or (   -- MARR derived-vs-explicit  (source lines 252-264)
                      t.external_event_key is null
                  and s.event_type = 'MARR' and s.is_derived = true
                  and t.event_type = 'MARR' and t.is_derived = false and t.family_id is null
                  and coalesce(t.event_value, '') = coalesce(s.event_value, '')
                  and coalesce(t.date_raw, '') = coalesce(s.date_raw, '')
                  and coalesce(t.year_from, (-32768)::smallint) = coalesce(s.year_from, (-32768)::smallint)
                  and coalesce(t.year_to, (-32768)::smallint) = coalesce(s.year_to, (-32768)::smallint)
                  and coalesce(t.place_id, (-1)::bigint) = coalesce(p.place_id, (-1)::bigint)
                  )
               or (   -- CHR -> BIRT  (source lines 265-275)
                      t.external_event_key is null
                  and s.event_type = 'CHR'
                  and t.event_type = 'BIRT' and t.is_derived = false
                  and coalesce(t.event_value, '') = coalesce(s.event_value, '')
                  and coalesce(t.date_raw, '') = coalesce(s.date_raw, '')
                  and coalesce(t.year_from, (-32768)::smallint) = coalesce(s.year_from, (-32768)::smallint)
                  and coalesce(t.year_to, (-32768)::smallint) = coalesce(s.year_to, (-32768)::smallint)
                  and coalesce(t.place_id, (-1)::bigint) = coalesce(p.place_id, (-1)::bigint)
                  )
               or (   -- DEAT (NO place check here)  (source lines 276-285)
                      t.external_event_key is null
                  and s.event_type = 'DEAT'
                  and t.event_type = 'DEAT' and t.is_derived = s.is_derived
                  and coalesce(t.event_value, '') = coalesce(s.event_value, '')
                  and coalesce(t.date_raw, '') = coalesce(s.date_raw, '')
                  and coalesce(t.year_from, (-32768)::smallint) = coalesce(s.year_from, (-32768)::smallint)
                  and coalesce(t.year_to, (-32768)::smallint) = coalesce(s.year_to, (-32768)::smallint)
                  )
           )
        where s.import_batch_id = p_import_batch_id
          and t.event_id is null

        -- Event MISSING_FROM_IMPORT  (source lines 291-347). Same match shapes
        -- as ADD but oriented from production events, comparing places by
        -- place_raw (production event's place is resolved via place_id -> place).
        union all
        select 'Event', 'MISSING_FROM_IMPORT', count(*)::bigint
        from genealogy.event t
        left join genealogy.place p on p.place_id = t.place_id
        left join genealogy.gedcom_import_event s
            on s.import_batch_id = p_import_batch_id
           and s.tree_person_id = t.person_id
           and (
                  s.external_event_key = t.external_event_key      -- source line 298
               or (   -- full attribute match on key-less events  (source lines 299-310)
                      t.external_event_key is null
                  and s.event_type = t.event_type
                  and coalesce(s.event_value, '') = coalesce(t.event_value, '')
                  and coalesce(s.date_raw, '') = coalesce(t.date_raw, '')
                  and coalesce(s.year_from, (-32768)::smallint) = coalesce(t.year_from, (-32768)::smallint)
                  and coalesce(s.year_to, (-32768)::smallint) = coalesce(t.year_to, (-32768)::smallint)
                  and coalesce(s.place_raw, '') = coalesce(p.place_raw, '')
                  and coalesce(s.family_id, v_zero_uuid) = coalesce(t.family_id, v_zero_uuid)
                  and coalesce(s.related_tree_person_id, v_zero_uuid) = coalesce(t.related_person_id, v_zero_uuid)
                  and s.is_derived = t.is_derived
                  )
               or (   -- MARR derived-vs-explicit  (source lines 311-323)
                      t.external_event_key is null
                  and s.event_type = 'MARR' and s.is_derived = true
                  and t.event_type = 'MARR' and t.is_derived = false and t.family_id is null
                  and coalesce(s.event_value, '') = coalesce(t.event_value, '')
                  and coalesce(s.date_raw, '') = coalesce(t.date_raw, '')
                  and coalesce(s.year_from, (-32768)::smallint) = coalesce(t.year_from, (-32768)::smallint)
                  and coalesce(s.year_to, (-32768)::smallint) = coalesce(t.year_to, (-32768)::smallint)
                  and coalesce(s.place_raw, '') = coalesce(p.place_raw, '')
                  )
               or (   -- CHR -> BIRT  (source lines 324-334)
                      t.external_event_key is null
                  and s.event_type = 'CHR'
                  and t.event_type = 'BIRT' and t.is_derived = false
                  and coalesce(s.event_value, '') = coalesce(t.event_value, '')
                  and coalesce(s.date_raw, '') = coalesce(t.date_raw, '')
                  and coalesce(s.year_from, (-32768)::smallint) = coalesce(t.year_from, (-32768)::smallint)
                  and coalesce(s.year_to, (-32768)::smallint) = coalesce(t.year_to, (-32768)::smallint)
                  and coalesce(s.place_raw, '') = coalesce(p.place_raw, '')
                  )
               or (   -- DEAT (NO place check here)  (source lines 335-344)
                      t.external_event_key is null
                  and s.event_type = 'DEAT'
                  and t.event_type = 'DEAT' and s.is_derived = t.is_derived
                  and coalesce(s.event_value, '') = coalesce(t.event_value, '')
                  and coalesce(s.date_raw, '') = coalesce(t.date_raw, '')
                  and coalesce(s.year_from, (-32768)::smallint) = coalesce(t.year_from, (-32768)::smallint)
                  and coalesce(s.year_to, (-32768)::smallint) = coalesce(t.year_to, (-32768)::smallint)
                  )
           )
        where t.tree_id = v_tree_id
          and s.row_number is null

        -- EventCitation ADD / REPLACE  (source lines 349-363). A staged
        -- citation whose event has no matching production event (by person +
        -- external key) is an ADD; otherwise the citation REPLACEs the
        -- production event's citations.
        union all
        select
            'EventCitation',
            case when t.event_id is null then 'ADD' else 'REPLACE' end,
            count(*)::bigint
        from genealogy.gedcom_import_event_citation c
        join genealogy.gedcom_import_event s
            on s.import_batch_id = c.import_batch_id
           and s.row_number = c.event_row_number
        left join genealogy.event t
            on t.tree_id = v_tree_id
           and t.person_id = s.tree_person_id
           and t.external_event_key = s.external_event_key
        where c.import_batch_id = p_import_batch_id
        group by case when t.event_id is null then 'ADD' else 'REPLACE' end
    ) as x (entity_type, change_type, row_count)
    where x.row_count > 0
    order by x.entity_type, x.change_type;
end;
$$;

comment on function genealogy.gedcom_import_preview_counts(uuid) is
    'Faithful port of ged.GetGedcomImportPreview result set 2 (change counts). '
    'STABLE/read-only; the STAGED->PREVIEWED flip lives in the .NET service. '
    'SpouseOf ADD is derived from genealogy.family unordered spouse pairs '
    '(see migration header for the forced deviation).';

-- ---------------------------------------------------------------------------
-- genealogy.gedcom_import_preview_person_samples(p_import_batch_id uuid)
--   Returns up to 200 person ADD/UPDATE rows with side-by-side staged vs
--   current values -- mirroring source result set 3 (source lines 370-390),
--   including TOP (200) -> LIMIT 200 and ORDER BY ChangeType, PrimaryDisplayName.
-- ---------------------------------------------------------------------------
create function genealogy.gedcom_import_preview_person_samples(p_import_batch_id uuid)
returns table (
    entity_type text,
    change_type text,
    tree_person_id uuid,
    external_id text,
    primary_display_name text,
    current_primary_display_name text,
    sex text,
    current_sex text
)
language plpgsql
stable
as $$
declare
    v_tree_id uuid;
begin
    select b.tree_id
    into v_tree_id
    from genealogy.gedcom_import_batch b
    where b.import_batch_id = p_import_batch_id;

    if v_tree_id is null then
        raise exception 'GEDCOM import batch not found.'
            using errcode = 'no_data_found';
    end if;

    return query
    select
        'Person'::text,
        case when t.person_id is null then 'ADD'::text else 'UPDATE'::text end,
        s.tree_person_id,
        s.external_id::text,
        s.primary_display_name,
        t.primary_display_name,
        s.sex::text,
        t.sex::text
    from genealogy.gedcom_import_person s
    left join genealogy.person t
        on t.tree_id = v_tree_id and t.person_id = s.tree_person_id
    where s.import_batch_id = p_import_batch_id
      and (
            t.person_id is null
         or coalesce(t.external_id, '') <> coalesce(s.external_id, '')
         or coalesce(t.sex::text, '') <> coalesce(s.sex::text, '')
         or coalesce(t.is_living, false) <> coalesce(s.is_living, false)
         or coalesce(t.primary_display_name, '') <> coalesce(s.primary_display_name, '')
         or coalesce(t.surname_normalized, '') <> coalesce(s.surname_normalized, '')
      )
    order by 2, s.primary_display_name
    limit 200;
end;
$$;

comment on function genealogy.gedcom_import_preview_person_samples(uuid) is
    'Faithful port of ged.GetGedcomImportPreview result set 3 (TOP 200 person '
    'ADD/UPDATE sample with staged-vs-current values). STABLE/read-only.';
