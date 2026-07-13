-- 0007_gedcom_import_readiness.sql
--
-- GEDCOM import READINESS gate function for the PostgreSQL genealogy
-- workspace. This is an ADVISORY report only: it computes four labelled gate
-- rows (blocker/warning/pass + a count) describing the quality of a staged
-- import batch (genealogy.gedcom_import_* tables from migration 0004, plus
-- genealogy.gedcom_import_duplicate_candidate from migration 0006). NOTHING
-- in this migration or the .NET service that calls it enforces these labels:
-- there is no confirmation token and `apply_gedcom_import` does not consult
-- this function. This mirrors the plan's explicit "no gate" decision
-- (docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md Phase 4) -- the labels exist to
-- inform a human/LLM reviewer, not to block anything.
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or
-- extend the schema by adding a new, higher-numbered migration instead of
-- changing an existing one. Editing an applied migration breaks the
-- schema_version journal and produces divergent databases.
--
-- SOURCE TO PORT (SQL Server): the readiness-metrics + gate-building halves of
-- McpServer/DnaAnalysis.McpServer/Tools/GedcomImportTools.cs
-- `GetGedcomImportReadinessReportAsync`:
--   ReadReadinessMetricsAsync (~lines 671-698) -- the four counts
--   BuildReadinessGates       (~lines 700-749) -- severity labels + report flags
--
-- Faithful gate policy (verbatim thresholds from the source):
--   high_confidence_duplicates : severity = 'blocker' if count > 0 else 'pass'
--                                 count = gedcom_import_duplicate_candidate rows
--                                 with status = 'suggested' AND score >= 0.9000
--                                 (this threshold is FIXED at 0.9000, independent
--                                 of any caller-supplied minimum duplicate score)
--   name_parsing_issues        : severity = 'warning' if count > 0 else 'pass'
--                                 count = gedcom_import_person_name_parsed rows
--                                 with parser_status <> 'OK'
--   date_warnings               : severity = 'warning' if count > 0 else 'pass'
--                                 count = gedcom_import_date_warning row count
--   scope_invalid                : severity = 'warning' if count > 0 else 'pass'
--                                 count = coalesce(batch.scope_invalid_count, 0)
--                                 NOTE: the current staging pipeline never
--                                 populates scope_invalid_count (it is always
--                                 NULL), so this gate always reports 0/'pass'
--                                 today. It stays wired (faithful to source)
--                                 in case a future exporter starts populating it.
--
-- This function intentionally reports ONLY the four gate rows above. The
-- report-level flags (canApplyWithoutReview, requiresExplicitConfirmation,
-- duplicateCount) and the duplicate-candidate REGENERATION call
-- (genealogy.generate_gedcom_import_duplicate_candidates, migration 0006) and
-- the STAGED/PREVIEWED -> WAITING_FOR_CONFIRMATION status transition all live
-- in the .NET GedcomReadinessService, mirroring the 0005 preview split (heavy
-- reads in a STABLE SQL function; side effects and small report shaping in the
-- .NET service).
--
-- STABLE (read-only): this function never modifies data.

create function genealogy.gedcom_import_readiness_gates(p_import_batch_id uuid)
returns table (gate text, severity text, gate_count bigint)
language plpgsql
stable
as $$
declare
    v_tree_id             uuid;
    v_scope_invalid_count  integer;
    v_high_conf_dup_count  bigint;
    v_name_issue_count     bigint;
    v_date_warning_count   bigint;
begin
    -- Resolve the batch's tree (NOT NULL column) as an existence probe; raise
    -- if the batch does not exist. Also reads scope_invalid_count in the same
    -- round trip (NULL -> 0, since the pipeline never populates it).
    select b.tree_id, coalesce(b.scope_invalid_count, 0)
    into v_tree_id, v_scope_invalid_count
    from genealogy.gedcom_import_batch b
    where b.import_batch_id = p_import_batch_id;

    if v_tree_id is null then
        raise exception 'GEDCOM import batch not found.'
            using errcode = 'no_data_found';
    end if;

    -- high_confidence_duplicates: fixed 0.9000 threshold, 'suggested' only.
    select count(*)
    into v_high_conf_dup_count
    from genealogy.gedcom_import_duplicate_candidate c
    where c.import_batch_id = p_import_batch_id
      and c.status = 'suggested'
      and c.score >= 0.9000;

    -- name_parsing_issues: anything the name parser did not mark OK.
    select count(*)
    into v_name_issue_count
    from genealogy.gedcom_import_person_name_parsed p
    where p.import_batch_id = p_import_batch_id
      and p.parser_status <> 'OK';

    -- date_warnings: every recorded warning row (unparsed/approximate/open-bound/range).
    select count(*)
    into v_date_warning_count
    from genealogy.gedcom_import_date_warning w
    where w.import_batch_id = p_import_batch_id;

    return query
    select 'high_confidence_duplicates'::text,
           case when v_high_conf_dup_count > 0 then 'blocker' else 'pass' end,
           v_high_conf_dup_count
    union all
    select 'name_parsing_issues'::text,
           case when v_name_issue_count > 0 then 'warning' else 'pass' end,
           v_name_issue_count
    union all
    select 'date_warnings'::text,
           case when v_date_warning_count > 0 then 'warning' else 'pass' end,
           v_date_warning_count
    union all
    select 'scope_invalid'::text,
           case when v_scope_invalid_count > 0 then 'warning' else 'pass' end,
           v_scope_invalid_count::bigint;
end;
$$;

comment on function genealogy.gedcom_import_readiness_gates(uuid) is
    'ADVISORY-ONLY readiness gates for a staged GEDCOM import batch (faithful '
    'port of the four-gate policy in GedcomImportTools.BuildReadinessGates / '
    'ReadReadinessMetricsAsync). Returns exactly 4 rows: '
    'high_confidence_duplicates (blocker/pass), name_parsing_issues '
    '(warning/pass), date_warnings (warning/pass), scope_invalid '
    '(warning/pass, always pass today -- documented dead gate). STABLE/'
    'read-only; nothing enforces these labels (no gate, per plan decision). '
    'Duplicate-candidate regeneration, report-level flags, and the status '
    'transition live in the .NET GedcomReadinessService.';
