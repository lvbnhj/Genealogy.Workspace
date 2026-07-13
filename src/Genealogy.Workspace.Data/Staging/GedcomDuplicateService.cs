using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// GEDCOM import DUPLICATE-CANDIDATE generate/list/detail/reject service. Ports
/// three SQL Server sources onto the migration 0006 PostgreSQL table
/// <c>genealogy.gedcom_import_duplicate_candidate</c> and function
/// <c>genealogy.generate_gedcom_import_duplicate_candidates</c>:
/// <list type="bullet">
/// <item><c>ged.GenerateGedcomImportDuplicateCandidates</c> -> <see cref="GenerateAsync"/>
/// (the scoring itself already lives in the migration 0006 SQL function; this
/// just invokes it, mirroring the 0005 preview split of side effect vs read).</item>
/// <item><c>ged.GetGedcomImportDuplicateCandidates</c> -> <see cref="ListCandidatesAsync"/>.</item>
/// <item><c>ged.GetGedcomImportDuplicateCandidateDetail</c> -> <see cref="GetCandidateDetailAsync"/>,
/// PLUS an EXPANDED per-side parents/children/spouses read that the source
/// proc does not have (per plan decision — see
/// docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10 Phase 4).</item>
/// </list>
/// <see cref="RejectAsync"/> has no direct source procedure; it is the
/// natural counterpart to the <c>status = 'rejected'</c> suppression the 0006
/// function already honours when regenerating candidates.
/// </summary>
public sealed class GedcomDuplicateService
{
    private const string PresentEventTypes = "('BIRT', 'CHR', 'DEAT', 'MARR')";

    private readonly NpgsqlConnectionFactory _connectionFactory;

    public GedcomDuplicateService(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Regenerates the <c>suggested</c> duplicate candidates for a staged
    /// import batch by invoking
    /// <c>genealogy.generate_gedcom_import_duplicate_candidates</c> (migration
    /// 0006) and returns the number of rows inserted. Throws a
    /// <see cref="PostgresException"/> (no_data_found) if the batch does not
    /// exist — the function itself raises that, so this method does not
    /// duplicate the check.
    /// </summary>
    public async Task<int> GenerateAsync(
        Guid importBatchId,
        decimal minScore = 0.75m,
        CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            SELECT genealogy.generate_gedcom_import_duplicate_candidates(@batch, @min_score);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("min_score", NpgsqlDbType.Numeric) { Value = minScore });

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Lists the <c>suggested</c> duplicate candidates for a batch at or above
    /// <paramref name="minScore"/>: a per-<c>candidate_scope</c> summary
    /// (mirrors source result set 1) and the ranked candidate rows, capped at
    /// <paramref name="topN"/> (clamped to 1..500) and decorated with display
    /// names for whichever sides are populated (mirrors source result set 2).
    /// </summary>
    /// <exception cref="GedcomImportBatchNotFoundException">
    /// No <c>genealogy.gedcom_import_batch</c> row has this id (mirrors the
    /// source procedure's <c>THROW 53032, 'GEDCOM import batch not found.', 1</c>).
    /// </exception>
    public async Task<GedcomDuplicateListResult> ListCandidatesAsync(
        Guid importBatchId,
        decimal minScore = 0.75m,
        int topN = 100,
        CancellationToken ct = default)
    {
        var clampedTopN = Math.Clamp(topN, 1, 500);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var treeId = await ResolveTreeIdAsync(connection, importBatchId, ct).ConfigureAwait(false);
        if (treeId is null)
        {
            throw new GedcomImportBatchNotFoundException(importBatchId);
        }

        var summary = await ReadSummaryAsync(connection, importBatchId, minScore, ct).ConfigureAwait(false);
        var candidates = await ReadCandidateRowsAsync(
            connection, importBatchId, treeId.Value, minScore, clampedTopN, ct).ConfigureAwait(false);

        return new GedcomDuplicateListResult(summary, candidates);
    }

    /// <summary>
    /// Reads the full detail of one duplicate candidate: the header (mirrors
    /// source detail result set 1), the BIRT/CHR/DEAT/MARR events for each
    /// present side (mirrors source detail result set 2), and an EXPANDED
    /// parents/children/spouses read per present side (beyond the source
    /// proc). Returns null if no candidate has this id.
    /// </summary>
    public async Task<GedcomDuplicateDetailResult?> GetCandidateDetailAsync(
        long duplicateCandidateId,
        CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var header = await ReadHeaderAsync(connection, duplicateCandidateId, ct).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        // The batch's tree scopes every "existing" (production) side read.
        // The candidate's own FK to gedcom_import_batch guarantees this
        // resolves; a null here would mean the batch was deleted concurrently
        // with the candidate still present, which the FK's ON DELETE CASCADE
        // (migration 0006) rules out.
        var treeId = await ResolveTreeIdAsync(connection, header.ImportBatchId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"GEDCOM import batch not found for duplicate candidate {duplicateCandidateId}'s import_batch_id {header.ImportBatchId}.");

        var events = new List<GedcomDuplicateDetailEvent>();
        var parents = new List<GedcomDuplicateRelative>();
        var children = new List<GedcomDuplicateRelative>();
        var spouses = new List<GedcomDuplicateSpouse>();

        // import1 side is always present.
        events.AddRange(await ReadStagedEventsAsync(
            connection, "import1", header.ImportBatchId, header.ImportTreePersonId1, ct).ConfigureAwait(false));
        parents.AddRange(await ReadStagedParentsAsync(
            connection, "import1", header.ImportBatchId, header.ImportTreePersonId1, ct).ConfigureAwait(false));
        children.AddRange(await ReadStagedChildrenAsync(
            connection, "import1", header.ImportBatchId, header.ImportTreePersonId1, ct).ConfigureAwait(false));
        spouses.AddRange(await ReadStagedSpousesAsync(
            connection, "import1", header.ImportBatchId, header.ImportTreePersonId1, ct).ConfigureAwait(false));

        // import2 side only exists for within_import candidates.
        if (header.CandidateScope == "within_import" && header.ImportTreePersonId2 is Guid import2Id)
        {
            events.AddRange(await ReadStagedEventsAsync(
                connection, "import2", header.ImportBatchId, import2Id, ct).ConfigureAwait(false));
            parents.AddRange(await ReadStagedParentsAsync(
                connection, "import2", header.ImportBatchId, import2Id, ct).ConfigureAwait(false));
            children.AddRange(await ReadStagedChildrenAsync(
                connection, "import2", header.ImportBatchId, import2Id, ct).ConfigureAwait(false));
            spouses.AddRange(await ReadStagedSpousesAsync(
                connection, "import2", header.ImportBatchId, import2Id, ct).ConfigureAwait(false));
        }

        // existing side only exists for import_vs_tree candidates.
        if (header.ExistingTreePersonId is Guid existingId)
        {
            events.AddRange(await ReadExistingEventsAsync(
                connection, treeId, existingId, ct).ConfigureAwait(false));
            parents.AddRange(await ReadExistingParentsAsync(
                connection, treeId, existingId, ct).ConfigureAwait(false));
            children.AddRange(await ReadExistingChildrenAsync(
                connection, treeId, existingId, ct).ConfigureAwait(false));
            spouses.AddRange(await ReadExistingSpousesAsync(
                connection, treeId, existingId, ct).ConfigureAwait(false));
        }

        return header with { Events = events, Parents = parents, Children = children, Spouses = spouses };
    }

    /// <summary>
    /// Rejects a duplicate candidate: sets <c>status = 'rejected'</c> and
    /// stamps <c>updated_at</c>. A rejected identity is suppressed by
    /// <c>genealogy.generate_gedcom_import_duplicate_candidates</c> on any
    /// later <see cref="GenerateAsync"/> call for the same batch (migration
    /// 0006's <c>not exists (... old.status = 'rejected')</c> clause).
    /// </summary>
    /// <exception cref="GedcomDuplicateCandidateNotFoundException">
    /// No <c>genealogy.gedcom_import_duplicate_candidate</c> row has this id.
    /// </exception>
    public async Task<GedcomDuplicateRejectResult> RejectAsync(
        long duplicateCandidateId,
        CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            UPDATE genealogy.gedcom_import_duplicate_candidate
            SET status = 'rejected', updated_at = now()
            WHERE duplicate_candidate_id = @id
            RETURNING duplicate_candidate_id, status;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = duplicateCandidateId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new GedcomDuplicateCandidateNotFoundException(duplicateCandidateId);
        }

        return new GedcomDuplicateRejectResult(
            DuplicateCandidateId: reader.GetFieldValue<long>(reader.GetOrdinal("duplicate_candidate_id")),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")));
    }

    // ------------------------------------------------------------------
    // Shared lookups
    // ------------------------------------------------------------------

    private static async Task<Guid?> ResolveTreeIdAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT tree_id FROM genealogy.gedcom_import_batch WHERE import_batch_id = @batch;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = importBatchId });

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null ? null : (Guid)result;
    }

    // ------------------------------------------------------------------
    // ListCandidatesAsync reads
    // ------------------------------------------------------------------

    private static async Task<List<GedcomDuplicateSummaryRow>> ReadSummaryAsync(
        NpgsqlConnection connection, Guid importBatchId, decimal minScore, CancellationToken ct)
    {
        // Mirrors source result set 1 (source lines 12-23).
        const string sql = """
            SELECT
                candidate_scope,
                count(*) AS candidate_count,
                sum(CASE WHEN score >= 0.9000 THEN 1 ELSE 0 END) AS high_confidence_count,
                sum(CASE WHEN score >= 0.7500 AND score < 0.9000 THEN 1 ELSE 0 END) AS probable_count,
                max(score) AS max_score
            FROM genealogy.gedcom_import_duplicate_candidate
            WHERE import_batch_id = @batch
              AND status = 'suggested'
              AND score >= @min_score
            GROUP BY candidate_scope
            ORDER BY candidate_scope;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("min_score", NpgsqlDbType.Numeric) { Value = minScore });

        var results = new List<GedcomDuplicateSummaryRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomDuplicateSummaryRow(
                CandidateScope: reader.GetFieldValue<string>(reader.GetOrdinal("candidate_scope")),
                CandidateCount: reader.GetFieldValue<long>(reader.GetOrdinal("candidate_count")),
                HighConfidenceCount: reader.GetFieldValue<long>(reader.GetOrdinal("high_confidence_count")),
                ProbableCount: reader.GetFieldValue<long>(reader.GetOrdinal("probable_count")),
                MaxScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("max_score"))));
        }

        return results;
    }

    private static async Task<List<GedcomDuplicateCandidateRow>> ReadCandidateRowsAsync(
        NpgsqlConnection connection, Guid importBatchId, Guid treeId, decimal minScore, int topN, CancellationToken ct)
    {
        // Mirrors source result set 2 (source lines 25-57). existing_tree_person_id
        // is a global genealogy.person.person_id (uuid PK), but joining it via the
        // batch's tree_id keeps the intent of the source's ep join explicit and
        // matches the composite-key ownership pattern used everywhere else.
        const string sql = """
            SELECT
                c.duplicate_candidate_id,
                c.candidate_scope,
                c.import_tree_person_id_1,
                p1.primary_display_name AS import_person_1_name,
                c.import_tree_person_id_2,
                p2.primary_display_name AS import_person_2_name,
                c.existing_tree_person_id,
                ep.primary_display_name AS existing_person_name,
                c.score,
                c.name_score,
                c.date_score,
                c.place_score,
                c.family_score,
                c.event_score,
                c.negative_score,
                c.evidence_for,
                c.evidence_against,
                c.recommended_action,
                c.status
            FROM genealogy.gedcom_import_duplicate_candidate c
            JOIN genealogy.gedcom_import_person p1
              ON p1.import_batch_id = c.import_batch_id
             AND p1.tree_person_id = c.import_tree_person_id_1
            LEFT JOIN genealogy.gedcom_import_person p2
              ON p2.import_batch_id = c.import_batch_id
             AND p2.tree_person_id = c.import_tree_person_id_2
            LEFT JOIN genealogy.person ep
              ON ep.tree_id = @tree
             AND ep.person_id = c.existing_tree_person_id
            WHERE c.import_batch_id = @batch
              AND c.status = 'suggested'
              AND c.score >= @min_score
            ORDER BY c.score DESC, c.duplicate_candidate_id
            LIMIT @top_n;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("min_score", NpgsqlDbType.Numeric) { Value = minScore });
        command.Parameters.Add(new NpgsqlParameter("top_n", NpgsqlDbType.Integer) { Value = topN });

        var results = new List<GedcomDuplicateCandidateRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomDuplicateCandidateRow(
                DuplicateCandidateId: reader.GetFieldValue<long>(reader.GetOrdinal("duplicate_candidate_id")),
                CandidateScope: reader.GetFieldValue<string>(reader.GetOrdinal("candidate_scope")),
                ImportTreePersonId1: reader.GetFieldValue<Guid>(reader.GetOrdinal("import_tree_person_id_1")),
                ImportPerson1Name: reader.GetNullableString("import_person_1_name"),
                ImportTreePersonId2: reader.GetNullableValue<Guid>("import_tree_person_id_2"),
                ImportPerson2Name: reader.GetNullableString("import_person_2_name"),
                ExistingTreePersonId: reader.GetNullableValue<Guid>("existing_tree_person_id"),
                ExistingPersonName: reader.GetNullableString("existing_person_name"),
                Score: reader.GetFieldValue<decimal>(reader.GetOrdinal("score")),
                NameScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("name_score")),
                DateScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("date_score")),
                PlaceScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("place_score")),
                FamilyScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("family_score")),
                EventScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("event_score")),
                NegativeScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("negative_score")),
                EvidenceFor: reader.GetNullableString("evidence_for"),
                EvidenceAgainst: reader.GetNullableString("evidence_against"),
                RecommendedAction: reader.GetFieldValue<string>(reader.GetOrdinal("recommended_action")),
                Status: reader.GetFieldValue<string>(reader.GetOrdinal("status"))));
        }

        return results;
    }

    // ------------------------------------------------------------------
    // GetCandidateDetailAsync: header
    // ------------------------------------------------------------------

    private static async Task<GedcomDuplicateDetailResult?> ReadHeaderAsync(
        NpgsqlConnection connection, long duplicateCandidateId, CancellationToken ct)
    {
        // Mirrors source detail result set 1 (source lines 7-42).
        const string sql = """
            SELECT
                c.duplicate_candidate_id,
                c.import_batch_id,
                c.candidate_scope,
                c.import_tree_person_id_1,
                p1.primary_display_name AS import_person_1_name,
                p1.external_id AS import_person_1_external_id,
                c.import_tree_person_id_2,
                p2.primary_display_name AS import_person_2_name,
                p2.external_id AS import_person_2_external_id,
                c.existing_tree_person_id,
                ep.primary_display_name AS existing_person_name,
                ep.external_id AS existing_person_external_id,
                c.score,
                c.name_score,
                c.date_score,
                c.place_score,
                c.family_score,
                c.event_score,
                c.negative_score,
                c.evidence_for,
                c.evidence_against,
                c.recommended_action,
                c.status,
                c.created_at,
                c.updated_at
            FROM genealogy.gedcom_import_duplicate_candidate c
            JOIN genealogy.gedcom_import_person p1
              ON p1.import_batch_id = c.import_batch_id
             AND p1.tree_person_id = c.import_tree_person_id_1
            LEFT JOIN genealogy.gedcom_import_person p2
              ON p2.import_batch_id = c.import_batch_id
             AND p2.tree_person_id = c.import_tree_person_id_2
            LEFT JOIN genealogy.person ep
              ON ep.person_id = c.existing_tree_person_id
            WHERE c.duplicate_candidate_id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = duplicateCandidateId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new GedcomDuplicateDetailResult(
            DuplicateCandidateId: reader.GetFieldValue<long>(reader.GetOrdinal("duplicate_candidate_id")),
            ImportBatchId: reader.GetFieldValue<Guid>(reader.GetOrdinal("import_batch_id")),
            CandidateScope: reader.GetFieldValue<string>(reader.GetOrdinal("candidate_scope")),
            ImportTreePersonId1: reader.GetFieldValue<Guid>(reader.GetOrdinal("import_tree_person_id_1")),
            ImportPerson1Name: reader.GetNullableString("import_person_1_name"),
            ImportPerson1ExternalId: reader.GetNullableString("import_person_1_external_id"),
            ImportTreePersonId2: reader.GetNullableValue<Guid>("import_tree_person_id_2"),
            ImportPerson2Name: reader.GetNullableString("import_person_2_name"),
            ImportPerson2ExternalId: reader.GetNullableString("import_person_2_external_id"),
            ExistingTreePersonId: reader.GetNullableValue<Guid>("existing_tree_person_id"),
            ExistingPersonName: reader.GetNullableString("existing_person_name"),
            ExistingPersonExternalId: reader.GetNullableString("existing_person_external_id"),
            Score: reader.GetFieldValue<decimal>(reader.GetOrdinal("score")),
            NameScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("name_score")),
            DateScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("date_score")),
            PlaceScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("place_score")),
            FamilyScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("family_score")),
            EventScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("event_score")),
            NegativeScore: reader.GetFieldValue<decimal>(reader.GetOrdinal("negative_score")),
            EvidenceFor: reader.GetNullableString("evidence_for"),
            EvidenceAgainst: reader.GetNullableString("evidence_against"),
            RecommendedAction: reader.GetFieldValue<string>(reader.GetOrdinal("recommended_action")),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            UpdatedAt: reader.GetNullableValue<DateTimeOffset>("updated_at"),
            Events: [],
            Parents: [],
            Children: [],
            Spouses: []);
    }

    // ------------------------------------------------------------------
    // GetCandidateDetailAsync: events (staged sides)
    // ------------------------------------------------------------------

    private static async Task<List<GedcomDuplicateDetailEvent>> ReadStagedEventsAsync(
        NpgsqlConnection connection, string side, Guid importBatchId, Guid treePersonId, CancellationToken ct)
    {
        // Mirrors source detail result set 2's import1/import2 branches
        // (source lines 44-107): STUFF(... FOR XML PATH('')) becomes
        // string_agg over a top-3 subquery; the EXISTS check is identical.
        var sql = $"""
            SELECT
                e.event_type,
                e.date_raw,
                e.year_from,
                e.year_to,
                e.place_raw,
                e.event_value,
                EXISTS (
                    SELECT 1 FROM genealogy.gedcom_import_event_citation ec
                    WHERE ec.import_batch_id = e.import_batch_id
                      AND ec.event_row_number = e.row_number
                ) AS has_source_citation,
                (
                    SELECT string_agg(x.summary, '; ' ORDER BY x.row_number)
                    FROM (
                        SELECT ec.row_number,
                               concat_ws(', ', nullif(ec.source_title, ''), nullif(ec.page, ''), nullif(ec.source_ref, '')) AS summary
                        FROM genealogy.gedcom_import_event_citation ec
                        WHERE ec.import_batch_id = e.import_batch_id
                          AND ec.event_row_number = e.row_number
                        ORDER BY ec.row_number
                        LIMIT 3
                    ) x
                ) AS citation_summary
            FROM genealogy.gedcom_import_event e
            WHERE e.import_batch_id = @batch
              AND e.tree_person_id = @person
              AND e.event_type IN {PresentEventTypes}
            ORDER BY e.year_from, e.event_type;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = treePersonId });

        var results = new List<GedcomDuplicateDetailEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapEvent(reader, side));
        }

        return results;
    }

    // ------------------------------------------------------------------
    // GetCandidateDetailAsync: events (existing/production side)
    // ------------------------------------------------------------------

    private static async Task<List<GedcomDuplicateDetailEvent>> ReadExistingEventsAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken ct)
    {
        // Mirrors source detail result set 2's existing branch (source lines
        // 111-139): place comes via place_id -> place.place_raw.
        var sql = $"""
            SELECT
                e.event_type,
                e.date_raw,
                e.year_from,
                e.year_to,
                p.place_raw,
                e.event_value,
                EXISTS (
                    SELECT 1 FROM genealogy.event_citation ec WHERE ec.event_id = e.event_id
                ) AS has_source_citation,
                (
                    SELECT string_agg(x.summary, '; ' ORDER BY x.event_citation_id)
                    FROM (
                        SELECT ec.event_citation_id,
                               concat_ws(', ', nullif(ec.source_title, ''), nullif(ec.page, ''), nullif(ec.source_ref, '')) AS summary
                        FROM genealogy.event_citation ec
                        WHERE ec.event_id = e.event_id
                        ORDER BY ec.event_citation_id
                        LIMIT 3
                    ) x
                ) AS citation_summary
            FROM genealogy.event e
            LEFT JOIN genealogy.place p ON p.place_id = e.place_id
            WHERE e.tree_id = @tree
              AND e.person_id = @person
              AND e.event_type IN {PresentEventTypes}
            ORDER BY e.year_from, e.event_type;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });

        var results = new List<GedcomDuplicateDetailEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapEvent(reader, "existing"));
        }

        return results;
    }

    private static GedcomDuplicateDetailEvent MapEvent(NpgsqlDataReader reader, string side) =>
        new(
            Side: side,
            EventType: reader.GetFieldValue<string>(reader.GetOrdinal("event_type")),
            DateRaw: reader.GetNullableString("date_raw"),
            YearFrom: reader.GetNullableValue<short>("year_from"),
            YearTo: reader.GetNullableValue<short>("year_to"),
            PlaceRaw: reader.GetNullableString("place_raw"),
            EventValue: reader.GetNullableString("event_value"),
            HasSourceCitation: reader.GetFieldValue<bool>(reader.GetOrdinal("has_source_citation")),
            CitationSummary: reader.GetNullableString("citation_summary"));

    // ------------------------------------------------------------------
    // GetCandidateDetailAsync: EXPANDED parents/children/spouses (staged sides)
    // ------------------------------------------------------------------

    private static async Task<List<GedcomDuplicateRelative>> ReadStagedParentsAsync(
        NpgsqlConnection connection, string side, Guid importBatchId, Guid treePersonId, CancellationToken ct)
    {
        const string sql = """
            SELECT po.parent_tree_person_id AS person_id, pp.primary_display_name AS display_name
            FROM genealogy.gedcom_import_parent_of po
            JOIN genealogy.gedcom_import_person pp
              ON pp.import_batch_id = po.import_batch_id
             AND pp.tree_person_id = po.parent_tree_person_id
            WHERE po.import_batch_id = @batch
              AND po.child_tree_person_id = @person;
            """;

        return await ReadRelativesAsync(connection, sql, side, importBatchId, treePersonId, ct).ConfigureAwait(false);
    }

    private static async Task<List<GedcomDuplicateRelative>> ReadStagedChildrenAsync(
        NpgsqlConnection connection, string side, Guid importBatchId, Guid treePersonId, CancellationToken ct)
    {
        const string sql = """
            SELECT po.child_tree_person_id AS person_id, cp.primary_display_name AS display_name
            FROM genealogy.gedcom_import_parent_of po
            JOIN genealogy.gedcom_import_person cp
              ON cp.import_batch_id = po.import_batch_id
             AND cp.tree_person_id = po.child_tree_person_id
            WHERE po.import_batch_id = @batch
              AND po.parent_tree_person_id = @person;
            """;

        return await ReadRelativesAsync(connection, sql, side, importBatchId, treePersonId, ct).ConfigureAwait(false);
    }

    private static async Task<List<GedcomDuplicateRelative>> ReadRelativesAsync(
        NpgsqlConnection connection, string sql, string side, Guid importBatchId, Guid treePersonId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = treePersonId });

        var results = new List<GedcomDuplicateRelative>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomDuplicateRelative(
                Side: side,
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                DisplayName: reader.GetNullableString("display_name")));
        }

        return results;
    }

    private static async Task<List<GedcomDuplicateSpouse>> ReadStagedSpousesAsync(
        NpgsqlConnection connection, string side, Guid importBatchId, Guid treePersonId, CancellationToken ct)
    {
        // Staged spouse edges come only from gedcom_import_family
        // (spouse1/spouse2 are nullable there, unlike production family), so
        // the "other spouse" id can be null; that row is excluded.
        const string sql = """
            SELECT other.person_id, sp.primary_display_name AS display_name, other.marriage_year
            FROM (
                SELECT
                    f.family_id,
                    (CASE WHEN f.spouse1_tree_person_id = @person
                          THEN f.spouse2_tree_person_id ELSE f.spouse1_tree_person_id END) AS person_id,
                    f.marriage_year
                FROM genealogy.gedcom_import_family f
                WHERE f.import_batch_id = @batch
                  AND (f.spouse1_tree_person_id = @person OR f.spouse2_tree_person_id = @person)
            ) other
            LEFT JOIN genealogy.gedcom_import_person sp
              ON sp.import_batch_id = @batch
             AND sp.tree_person_id = other.person_id
            WHERE other.person_id IS NOT NULL;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = treePersonId });

        var results = new List<GedcomDuplicateSpouse>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomDuplicateSpouse(
                Side: side,
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                DisplayName: reader.GetNullableString("display_name"),
                MarriageYear: reader.GetNullableValue<short>("marriage_year")));
        }

        return results;
    }

    // ------------------------------------------------------------------
    // GetCandidateDetailAsync: EXPANDED parents/children/spouses (existing side)
    // ------------------------------------------------------------------

    private static async Task<List<GedcomDuplicateRelative>> ReadExistingParentsAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken ct)
    {
        const string sql = """
            SELECT pc.parent_person_id AS person_id, pp.primary_display_name AS display_name
            FROM genealogy.parent_child pc
            JOIN genealogy.person pp
              ON pp.tree_id = pc.tree_id
             AND pp.person_id = pc.parent_person_id
            WHERE pc.tree_id = @tree
              AND pc.child_person_id = @person;
            """;

        return await ReadExistingRelativesAsync(connection, sql, treeId, personId, ct).ConfigureAwait(false);
    }

    private static async Task<List<GedcomDuplicateRelative>> ReadExistingChildrenAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken ct)
    {
        const string sql = """
            SELECT pc.child_person_id AS person_id, cp.primary_display_name AS display_name
            FROM genealogy.parent_child pc
            JOIN genealogy.person cp
              ON cp.tree_id = pc.tree_id
             AND cp.person_id = pc.child_person_id
            WHERE pc.tree_id = @tree
              AND pc.parent_person_id = @person;
            """;

        return await ReadExistingRelativesAsync(connection, sql, treeId, personId, ct).ConfigureAwait(false);
    }

    private static async Task<List<GedcomDuplicateRelative>> ReadExistingRelativesAsync(
        NpgsqlConnection connection, string sql, Guid treeId, Guid personId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });

        var results = new List<GedcomDuplicateRelative>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomDuplicateRelative(
                Side: "existing",
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                DisplayName: reader.GetNullableString("display_name")));
        }

        return results;
    }

    private static async Task<List<GedcomDuplicateSpouse>> ReadExistingSpousesAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken ct)
    {
        // spouse1_person_id/spouse2_person_id are NOT NULL on production
        // genealogy.family (unlike the staging table), so the "other spouse"
        // id is always present here.
        const string sql = """
            SELECT
                (CASE WHEN f.spouse1_person_id = @person
                      THEN f.spouse2_person_id ELSE f.spouse1_person_id END) AS person_id,
                sp.primary_display_name AS display_name,
                f.marriage_year
            FROM genealogy.family f
            JOIN genealogy.person sp
              ON sp.tree_id = f.tree_id
             AND sp.person_id = (CASE WHEN f.spouse1_person_id = @person
                                       THEN f.spouse2_person_id ELSE f.spouse1_person_id END)
            WHERE f.tree_id = @tree
              AND (f.spouse1_person_id = @person OR f.spouse2_person_id = @person);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });

        var results = new List<GedcomDuplicateSpouse>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomDuplicateSpouse(
                Side: "existing",
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                DisplayName: reader.GetNullableString("display_name"),
                MarriageYear: reader.GetNullableValue<short>("marriage_year")));
        }

        return results;
    }
}
