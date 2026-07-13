-- 0008_gedcom_apply.sql
--
-- GEDCOM import APPLY function for the PostgreSQL genealogy workspace. A single
-- plpgsql function transactionally and idempotently applies a staged import
-- batch (genealogy.gedcom_import_* tables from migration 0004) into the
-- production tree (genealogy.person / person_name / place / family /
-- family_child / parent_child / event / event_citation from migration 0002).
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or extend
-- the schema by adding a new, higher-numbered migration instead of changing an
-- existing one. Editing an applied migration breaks the schema_version journal
-- and produces divergent databases.
--
-- SOURCE TO PORT (SQL Server): ged.ApplyGedcomImport
--   (Database/Procedures/ged/ApplyGedcomImport.sql). This ports that
--   procedure's per-entity upsert logic, MINUS the following deliberately
--   dropped legacy cruft (see plan section 10, Phase 4):
--     * the @DryRun branch (dry-run is handled by the .NET service calling the
--       0005 preview functions separately);
--     * the readiness / duplicate gate (NO readiness or duplicate check happens
--       inside apply -- faithful to source, which had none either);
--     * the three MARR/CHR/DEAT legacy NULL-external-key reconciliation UPDATEs
--       (dead code here: every staged event carries a deterministic
--       external_event_key, so there are no NULL-key production rows to
--       reconcile);
--     * the ged.TreeRelationship MERGE and the ged.TreeSpouseOf writes (neither
--       table exists in the Phase 2 production schema -- parent edges live only
--       in genealogy.parent_child, spouse pairs only on genealogy.family);
--     * ged.TreePersonLink / TreeNameVariant handling (those tables do not
--       exist in Phase 2).
--
-- STATUS GUARD: the only guard is that the batch status must be one of
--   ('STAGED','PREVIEWED','WAITING_FOR_CONFIRMATION'). This blocks re-applying
--   an already-APPLIED batch and applying a CANCELLED one. There is no
--   confirmation token.
--
-- ATOMICITY: the whole body runs in the caller's transaction. Any RAISE (or any
--   constraint violation) aborts the transaction, so either every write lands or
--   none does -- there are no partial applies.
--
-- IDEMPOTENCY: deterministic person_id / family_id plus ON CONFLICT / NOT EXISTS
--   guards, and event matching on the stable (tree_id, person_id,
--   external_event_key) key, mean that applying the same deterministic data a
--   second time (via a fresh batch, or after resetting the batch status)
--   produces zero net new production rows. The event_citation delete+reinsert is
--   allowed to churn surrogate ids/timestamps but never row counts.

create function genealogy.apply_gedcom_import(
    p_import_batch_id uuid,
    p_delete_missing  boolean default false
)
returns table (entity_type text, change_type text, row_count bigint)
language plpgsql
as $$
declare
    v_tree_id        uuid;
    v_status         varchar(30);
    v_root_person_id uuid;

    -- Per-entity change counters. All default to 0 so the final summary emits
    -- only the rows that actually changed.
    v_place_add             bigint := 0;
    v_person_add            bigint := 0;
    v_person_update         bigint := 0;
    v_person_name_add       bigint := 0;
    v_family_add            bigint := 0;
    v_family_update         bigint := 0;
    v_family_child_add      bigint := 0;
    v_parent_of_add         bigint := 0;
    v_event_add             bigint := 0;
    v_event_update          bigint := 0;
    v_event_citation_repl   bigint := 0;

    -- delete_missing counters (stay 0 unless the delete branch runs).
    v_event_delete          bigint := 0;
    v_family_child_delete   bigint := 0;
    v_parent_of_delete      bigint := 0;
    v_person_name_delete    bigint := 0;
    v_family_delete         bigint := 0;
    v_person_delete         bigint := 0;
begin
    -- ---------------------------------------------------------------------
    -- Step 0: resolve the batch, its tree and root, and enforce the status
    -- guard. A missing batch raises no_data_found; a non-applyable status
    -- (e.g. APPLIED / CANCELLED) raises and aborts before any write.
    -- ---------------------------------------------------------------------
    select b.tree_id, b.status, b.root_person_id
    into v_tree_id, v_status, v_root_person_id
    from genealogy.gedcom_import_batch b
    where b.import_batch_id = p_import_batch_id;

    if v_tree_id is null then
        raise exception 'GEDCOM import batch % not found.', p_import_batch_id
            using errcode = 'no_data_found';
    end if;

    if v_status not in ('STAGED', 'PREVIEWED', 'WAITING_FOR_CONFIRMATION') then
        raise exception
            'GEDCOM import batch % is not in an applyable status (status = %).',
            p_import_batch_id, v_status
            using errcode = 'invalid_parameter_value';
    end if;

    -- ---------------------------------------------------------------------
    -- Step 1: delete_missing branch. With the default p_delete_missing = false
    -- NOTHING is ever deleted. When true, a SAFETY VALVE runs first: if the
    -- batch declares a root person that is NOT present among the staged
    -- persons, refuse -- this guards against a truncated import wiping the
    -- tree. Then production rows for persons/families in this tree that are
    -- ABSENT from staging are hard-deleted in FK-safe order.
    -- ---------------------------------------------------------------------
    if p_delete_missing then
        if v_root_person_id is not null
           and not exists (
               select 1
               from genealogy.gedcom_import_person s
               where s.import_batch_id = p_import_batch_id
                 and s.tree_person_id = v_root_person_id
           ) then
            raise exception
                'Refusing to delete missing GEDCOM persons because the root person % is missing from the import.',
                v_root_person_id
                using errcode = 'raise_exception';
        end if;

        drop table if exists _apply_missing_person, _apply_missing_family;

        -- Production persons in this tree that are absent from staging.
        create temp table _apply_missing_person on commit drop as
        select p.person_id
        from genealogy.person p
        where p.tree_id = v_tree_id
          and not exists (
              select 1
              from genealogy.gedcom_import_person s
              where s.import_batch_id = p_import_batch_id
                and s.tree_person_id = p.person_id
          );

        -- Production families that are absent from staging OR whose spouse is a
        -- missing person (the latter must go too, else the person delete below
        -- would violate the family spouse foreign key).
        create temp table _apply_missing_family on commit drop as
        select f.family_id
        from genealogy.family f
        where f.tree_id = v_tree_id
          and (
              not exists (
                  select 1
                  from genealogy.gedcom_import_family gf
                  where gf.import_batch_id = p_import_batch_id
                    and gf.family_id = f.family_id
              )
           or f.spouse1_person_id in (select person_id from _apply_missing_person)
           or f.spouse2_person_id in (select person_id from _apply_missing_person)
          );

        -- Clear the tree root if it points at a person about to be deleted, so
        -- the same-tree root foreign key (0002) does not block the delete.
        update genealogy.tree t
        set root_person_id = null
        where t.tree_id = v_tree_id
          and t.root_person_id in (select person_id from _apply_missing_person);

        -- event references person (person_id / related_person_id) and family;
        -- its GEDCOM citations cascade away automatically (ON DELETE CASCADE).
        delete from genealogy.event e
        where e.tree_id = v_tree_id
          and (
              e.person_id in (select person_id from _apply_missing_person)
           or e.related_person_id in (select person_id from _apply_missing_person)
           or e.family_id in (select family_id from _apply_missing_family)
          );
        get diagnostics v_event_delete = row_count;

        delete from genealogy.family_child fc
        where fc.tree_id = v_tree_id
          and (
              fc.family_id in (select family_id from _apply_missing_family)
           or fc.child_person_id in (select person_id from _apply_missing_person)
          );
        get diagnostics v_family_child_delete = row_count;

        delete from genealogy.parent_child pc
        where pc.tree_id = v_tree_id
          and (
              pc.parent_person_id in (select person_id from _apply_missing_person)
           or pc.child_person_id in (select person_id from _apply_missing_person)
          );
        get diagnostics v_parent_of_delete = row_count;

        delete from genealogy.person_name pn
        where pn.tree_id = v_tree_id
          and pn.person_id in (select person_id from _apply_missing_person);
        get diagnostics v_person_name_delete = row_count;

        delete from genealogy.family f
        where f.tree_id = v_tree_id
          and f.family_id in (select family_id from _apply_missing_family);
        get diagnostics v_family_delete = row_count;

        delete from genealogy.person p
        where p.tree_id = v_tree_id
          and p.person_id in (select person_id from _apply_missing_person);
        get diagnostics v_person_delete = row_count;
    end if;

    -- ---------------------------------------------------------------------
    -- Step 2: place (GLOBAL, no tree_id). Insert distinct staged places whose
    -- place_raw is not yet present. Families and events below resolve their
    -- place_id by joining on place_raw, so places must land first.
    -- ---------------------------------------------------------------------
    insert into genealogy.place (place_raw, place_normalized)
    select distinct s.place_raw, s.place_normalized
    from genealogy.gedcom_import_place s
    where s.import_batch_id = p_import_batch_id
      and not exists (
          select 1 from genealogy.place p where p.place_raw = s.place_raw
      );
    get diagnostics v_place_add = row_count;

    -- ---------------------------------------------------------------------
    -- Step 3: person upsert by person_id (= staged tree_person_id). The
    -- DO UPDATE ... WHERE guard fires only when a mutable column actually
    -- differs (IS DISTINCT FROM handles NULLs), so re-applying unchanged data
    -- touches no rows. The (xmax = 0) trick in RETURNING separates freshly
    -- inserted tuples (xmax 0) from updated ones.
    -- ---------------------------------------------------------------------
    with upsert as (
        insert into genealogy.person (
            person_id, tree_id, external_id, sex, is_living,
            primary_display_name, surname_normalized
        )
        select
            s.tree_person_id, v_tree_id, s.external_id, s.sex, s.is_living,
            s.primary_display_name, s.surname_normalized
        from genealogy.gedcom_import_person s
        where s.import_batch_id = p_import_batch_id
        on conflict (person_id) do update
            set external_id          = excluded.external_id,
                sex                   = excluded.sex,
                is_living             = excluded.is_living,
                primary_display_name  = excluded.primary_display_name,
                surname_normalized    = excluded.surname_normalized,
                updated_at            = now()
            where person.external_id          is distinct from excluded.external_id
               or person.sex                  is distinct from excluded.sex
               or person.is_living            is distinct from excluded.is_living
               or person.primary_display_name is distinct from excluded.primary_display_name
               or person.surname_normalized   is distinct from excluded.surname_normalized
        returning (xmax::text::bigint = 0) as inserted
    )
    select
        count(*) filter (where inserted),
        count(*) filter (where not inserted)
    into v_person_add, v_person_update
    from upsert;

    -- ---------------------------------------------------------------------
    -- Step 4: person_name -- strictly ADDITIVE (never delete stale variants).
    -- New (person_id, script_code, full_name_normalized) rows are inserted;
    -- existing ones are left untouched (ON CONFLICT DO NOTHING), so the source
    -- proc's per-variant UPDATE is intentionally not ported (the Phase 4
    -- summary reports PersonName ADD only). A staged name is inserted as
    -- primary only when the person has no primary yet, honouring the one-primary
    -- partial unique index and "prefer keeping the existing primary".
    -- ---------------------------------------------------------------------
    insert into genealogy.person_name (
        tree_id, person_id, script_code, name_type, given, surname,
        full_name, full_name_normalized, is_primary
    )
    select
        v_tree_id, s.tree_person_id, s.script_code, s.name_type, s.given, s.surname,
        s.full_name, s.full_name_normalized,
        s.is_primary and not exists (
            select 1 from genealogy.person_name pn
            where pn.person_id = s.tree_person_id and pn.is_primary
        )
    from genealogy.gedcom_import_person_name s
    where s.import_batch_id = p_import_batch_id
    on conflict (person_id, script_code, full_name_normalized) do nothing;
    get diagnostics v_person_name_add = row_count;

    -- ---------------------------------------------------------------------
    -- Step 5: family upsert by family_id. Only staged families where BOTH
    -- spouses resolve to an existing person in this tree are applied (the two
    -- inner joins drop unresolved families silently, like the source).
    -- marriage_place_id is resolved via a LEFT JOIN on place_raw.
    -- ---------------------------------------------------------------------
    with upsert as (
        insert into genealogy.family (
            family_id, tree_id, spouse1_person_id, spouse2_person_id,
            marriage_date_raw, marriage_year, marriage_place_raw,
            marriage_place_id, notes
        )
        select
            f.family_id, v_tree_id, f.spouse1_tree_person_id, f.spouse2_tree_person_id,
            f.marriage_date_raw, f.marriage_year, f.marriage_place_raw,
            pl.place_id, f.notes
        from genealogy.gedcom_import_family f
        join genealogy.person p1
          on p1.tree_id = v_tree_id and p1.person_id = f.spouse1_tree_person_id
        join genealogy.person p2
          on p2.tree_id = v_tree_id and p2.person_id = f.spouse2_tree_person_id
        left join genealogy.place pl on pl.place_raw = f.marriage_place_raw
        where f.import_batch_id = p_import_batch_id
          and f.spouse1_tree_person_id is not null
          and f.spouse2_tree_person_id is not null
        on conflict (family_id) do update
            set spouse1_person_id  = excluded.spouse1_person_id,
                spouse2_person_id  = excluded.spouse2_person_id,
                marriage_date_raw  = excluded.marriage_date_raw,
                marriage_year      = excluded.marriage_year,
                marriage_place_raw = excluded.marriage_place_raw,
                marriage_place_id  = excluded.marriage_place_id,
                notes              = excluded.notes,
                updated_at         = now()
            where family.spouse1_person_id  is distinct from excluded.spouse1_person_id
               or family.spouse2_person_id  is distinct from excluded.spouse2_person_id
               or family.marriage_date_raw  is distinct from excluded.marriage_date_raw
               or family.marriage_year      is distinct from excluded.marriage_year
               or family.marriage_place_raw is distinct from excluded.marriage_place_raw
               or family.marriage_place_id  is distinct from excluded.marriage_place_id
               or family.notes              is distinct from excluded.notes
        returning (xmax::text::bigint = 0) as inserted
    )
    select
        count(*) filter (where inserted),
        count(*) filter (where not inserted)
    into v_family_add, v_family_update
    from upsert;

    -- ---------------------------------------------------------------------
    -- Step 6: family_child. Additive; the joins ensure both the family and the
    -- child person exist in this tree before the edge is inserted.
    -- ---------------------------------------------------------------------
    insert into genealogy.family_child (tree_id, family_id, child_person_id)
    select v_tree_id, s.family_id, s.child_tree_person_id
    from genealogy.gedcom_import_family_child s
    join genealogy.family f
      on f.tree_id = v_tree_id and f.family_id = s.family_id
    join genealogy.person c
      on c.tree_id = v_tree_id and c.person_id = s.child_tree_person_id
    where s.import_batch_id = p_import_batch_id
    on conflict (family_id, child_person_id) do nothing;
    get diagnostics v_family_child_add = row_count;

    -- ---------------------------------------------------------------------
    -- Step 7: parent_child (the sole parent-edge write; TreeRelationship
    -- dropped). Additive; joins ensure both endpoints exist in this tree.
    -- ---------------------------------------------------------------------
    insert into genealogy.parent_child (
        tree_id, parent_person_id, child_person_id, relation_type
    )
    select v_tree_id, s.parent_tree_person_id, s.child_tree_person_id, s.relation_type
    from genealogy.gedcom_import_parent_of s
    join genealogy.person p
      on p.tree_id = v_tree_id and p.person_id = s.parent_tree_person_id
    join genealogy.person c
      on c.tree_id = v_tree_id and c.person_id = s.child_tree_person_id
    where s.import_batch_id = p_import_batch_id
    on conflict (parent_person_id, child_person_id, relation_type) do nothing;
    get diagnostics v_parent_of_add = row_count;

    -- ---------------------------------------------------------------------
    -- Step 8: event upsert. event_id is a non-deterministic identity, so the
    -- stable match key is (tree_id, person_id, external_event_key) -- always set
    -- by the parser. First UPDATE the mutable columns of matched events (guarded
    -- so nothing changes on a re-apply), then INSERT the staged events that have
    -- no matching production row. place_id / family_id / related_person_id are
    -- resolved via LEFT JOINs so an unresolved reference becomes NULL rather than
    -- a foreign-key failure (mirrors the source's LEFT JOINs).
    -- ---------------------------------------------------------------------
    update genealogy.event t
    set event_type        = s.event_type,
        event_value       = s.event_value,
        date_raw          = s.date_raw,
        date_from         = s.date_from,
        date_to           = s.date_to,
        year_from         = s.year_from,
        year_to           = s.year_to,
        place_id          = pl.place_id,
        family_id         = fam.family_id,
        related_person_id = rp.person_id,
        is_derived        = s.is_derived,
        notes             = s.notes
    from genealogy.gedcom_import_event s
    left join genealogy.place pl on pl.place_raw = s.place_raw
    left join genealogy.family fam
      on fam.tree_id = v_tree_id and fam.family_id = s.family_id
    left join genealogy.person rp
      on rp.tree_id = v_tree_id and rp.person_id = s.related_tree_person_id
    where s.import_batch_id = p_import_batch_id
      and t.tree_id = v_tree_id
      and t.person_id = s.tree_person_id
      and t.external_event_key = s.external_event_key
      and (
             t.event_type        is distinct from s.event_type
          or t.event_value       is distinct from s.event_value
          or t.date_raw          is distinct from s.date_raw
          or t.date_from         is distinct from s.date_from
          or t.date_to           is distinct from s.date_to
          or t.year_from         is distinct from s.year_from
          or t.year_to           is distinct from s.year_to
          or t.place_id          is distinct from pl.place_id
          or t.family_id         is distinct from fam.family_id
          or t.related_person_id is distinct from rp.person_id
          or t.is_derived        is distinct from s.is_derived
          or t.notes             is distinct from s.notes
      );
    get diagnostics v_event_update = row_count;

    with ins as (
        insert into genealogy.event (
            tree_id, person_id, event_type, event_value, date_raw, date_from,
            date_to, year_from, year_to, place_id, family_id, related_person_id,
            external_event_key, is_derived, notes
        )
        select
            v_tree_id, s.tree_person_id, s.event_type, s.event_value, s.date_raw,
            s.date_from, s.date_to, s.year_from, s.year_to, pl.place_id,
            fam.family_id, rp.person_id, s.external_event_key, s.is_derived, s.notes
        from genealogy.gedcom_import_event s
        left join genealogy.place pl on pl.place_raw = s.place_raw
        left join genealogy.family fam
          on fam.tree_id = v_tree_id and fam.family_id = s.family_id
        left join genealogy.person rp
          on rp.tree_id = v_tree_id and rp.person_id = s.related_tree_person_id
        where s.import_batch_id = p_import_batch_id
          and not exists (
              select 1
              from genealogy.event t
              where t.tree_id = v_tree_id
                and t.person_id = s.tree_person_id
                and t.external_event_key = s.external_event_key
          )
        returning 1
    )
    select count(*) into v_event_add from ins;

    -- ---------------------------------------------------------------------
    -- Step 9: event_citation -- delete-then-reinsert scoped to GEDCOM ONLY.
    -- Delete the GEDCOM citations of every production event matched by this
    -- batch (by external_event_key), then reinsert all staged citations mapped
    -- staged event_row_number -> production event_id. MANUAL citations are never
    -- touched. Reported as a single REPLACE count (= rows reinserted).
    -- ---------------------------------------------------------------------
    delete from genealogy.event_citation c
    using genealogy.event t, genealogy.gedcom_import_event s
    where c.source_origin = 'GEDCOM'
      and c.event_id = t.event_id
      and s.import_batch_id = p_import_batch_id
      and t.tree_id = v_tree_id
      and t.person_id = s.tree_person_id
      and t.external_event_key = s.external_event_key;

    with reins as (
        insert into genealogy.event_citation (
            event_id, source_origin, source_ref, source_title, page, quality,
            citation_date_raw, citation_text, note
        )
        select
            t.event_id, 'GEDCOM', c.source_ref, c.source_title, c.page, c.quality,
            c.citation_date_raw, c.citation_text, c.note
        from genealogy.gedcom_import_event_citation c
        join genealogy.gedcom_import_event s
          on s.import_batch_id = c.import_batch_id and s.row_number = c.event_row_number
        join genealogy.event t
          on t.tree_id = v_tree_id
         and t.person_id = s.tree_person_id
         and t.external_event_key = s.external_event_key
        where c.import_batch_id = p_import_batch_id
        returning 1
    )
    select count(*) into v_event_citation_repl from reins;

    -- ---------------------------------------------------------------------
    -- Step 10: point the tree root at the batch's root person (respecting the
    -- same-tree root foreign key from 0002: the person must exist in this tree).
    -- ---------------------------------------------------------------------
    update genealogy.tree t
    set root_person_id = v_root_person_id,
        updated_at     = now()
    where t.tree_id = v_tree_id
      and v_root_person_id is not null
      and t.root_person_id is distinct from v_root_person_id
      and exists (
          select 1 from genealogy.person p
          where p.tree_id = v_tree_id and p.person_id = v_root_person_id
      );

    -- ---------------------------------------------------------------------
    -- Step 11: flip the batch to APPLIED (atomic with every write above).
    -- ---------------------------------------------------------------------
    update genealogy.gedcom_import_batch
    set status = 'APPLIED', applied_at = now()
    where import_batch_id = p_import_batch_id;

    -- ---------------------------------------------------------------------
    -- Step 12: return the per-entity change summary, emitting only rows with a
    -- positive count, ordered by entity_type then change_type. Entity and
    -- change-type labels mirror the 0005 preview function and the source proc.
    -- ---------------------------------------------------------------------
    return query
    select v.entity_type, v.change_type, v.row_count
    from (
        values
            ('Place'::text,        'ADD'::text,     v_place_add),
            ('Person',             'ADD',           v_person_add),
            ('Person',             'UPDATE',        v_person_update),
            ('PersonName',         'ADD',           v_person_name_add),
            ('Family',             'ADD',           v_family_add),
            ('Family',             'UPDATE',        v_family_update),
            ('FamilyChild',        'ADD',           v_family_child_add),
            ('ParentOf',           'ADD',           v_parent_of_add),
            ('Event',              'ADD',           v_event_add),
            ('Event',              'UPDATE',        v_event_update),
            ('EventCitation',      'REPLACE',       v_event_citation_repl),
            ('Person',             'DELETE',        v_person_delete),
            ('PersonName',         'DELETE',        v_person_name_delete),
            ('Family',             'DELETE',        v_family_delete),
            ('FamilyChild',        'DELETE',        v_family_child_delete),
            ('ParentOf',           'DELETE',        v_parent_of_delete),
            ('Event',              'DELETE',        v_event_delete)
    ) as v(entity_type, change_type, row_count)
    where v.row_count > 0
    order by v.entity_type, v.change_type;
end;
$$;

comment on function genealogy.apply_gedcom_import(uuid, boolean) is
    'Transactionally and idempotently applies a staged GEDCOM import batch '
    '(genealogy.gedcom_import_* from 0004) into the production tree '
    '(genealogy.* from 0002). Faithful port of ged.ApplyGedcomImport minus the '
    'dry-run branch, the MARR/CHR/DEAT legacy reconciliation, and the '
    'TreeRelationship / TreeSpouseOf writes (no such tables in Phase 2). Guards '
    'only on batch status in (STAGED, PREVIEWED, WAITING_FOR_CONFIRMATION). With '
    'p_delete_missing = true, hard-deletes production persons/families absent '
    'from staging (after a root-present safety valve). Returns a per-entity '
    'ADD/UPDATE/REPLACE/DELETE change summary.';
