using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Ports the ADVISORY GEDCOM import readiness report from
/// <c>GedcomImportTools.GetGedcomImportReadinessReportAsync</c>
/// (McpServer/DnaAnalysis.McpServer/Tools/GedcomImportTools.cs). This is
/// NOT an enforcement gate: there is no confirmation token, and
/// <c>apply_gedcom_import</c> does not consult this report (the plan's
/// explicit "no gate" decision, docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md
/// §10 Phase 4). The four gate labels (blocker/warning/pass) exist purely to
/// inform a human/LLM reviewer.
///
/// The gate-count computation lives in the STABLE SQL function
/// <c>genealogy.gedcom_import_readiness_gates</c> (migration 0007), mirroring
/// the 0005 preview split: heavy/versioned reads in SQL, side effects (the
/// duplicate-candidate regeneration call and the status transition) and small
/// report shaping here.
/// </summary>
public sealed class GedcomReadinessService
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public GedcomReadinessService(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Builds the readiness report for a staged import batch. Mirrors the
    /// source method:
    /// <list type="number">
    /// <item>throws if the batch does not exist;</item>
    /// <item>regenerates duplicate candidates via
    /// <c>genealogy.generate_gedcom_import_duplicate_candidates</c> (migration
    /// 0006) using <paramref name="minDuplicateScore"/>, so the report
    /// reflects the current staged data (source lines ~137-138);</item>
    /// <item>reads the four advisory gates (source
    /// <c>ReadReadinessMetricsAsync</c> + <c>BuildReadinessGates</c>);</item>
    /// <item>computes the report-level flags: <c>canApplyWithoutReview</c> is
    /// true only when the high-confidence-duplicates gate count is zero,
    /// <c>requiresExplicitConfirmation</c> is always true (advisory-only, not
    /// enforced), and <c>duplicateCount</c> is every remaining 'suggested'
    /// candidate (which, after the regeneration above, all satisfy
    /// <c>score >= minDuplicateScore</c> by construction, since the
    /// generate function itself only ever inserts rows at or above its
    /// <c>p_min_score</c> argument);</item>
    /// <item>flips <c>STAGED</c>/<c>PREVIEWED</c> ->
    /// <c>WAITING_FOR_CONFIRMATION</c>, stamping
    /// <c>previewed_at = COALESCE(previewed_at, now())</c> so an
    /// already-previewed batch keeps its original preview timestamp.</item>
    /// </list>
    /// </summary>
    /// <exception cref="GedcomImportBatchNotFoundException">
    /// No <c>genealogy.gedcom_import_batch</c> row has this id.
    /// </exception>
    public async Task<GedcomReadinessReport> GetReadinessAsync(
        Guid importBatchId,
        decimal minDuplicateScore = 0.75m,
        CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        if (!await BatchExistsAsync(connection, importBatchId, ct).ConfigureAwait(false))
        {
            throw new GedcomImportBatchNotFoundException(importBatchId);
        }

        // Refresh duplicate candidates first so the high_confidence_duplicates
        // gate and duplicateCount below reflect the current staged data,
        // mirroring the source's own call to the generate procedure before
        // reading readiness metrics.
        await GenerateDuplicateCandidatesAsync(connection, importBatchId, minDuplicateScore, ct).ConfigureAwait(false);

        var gates = await ReadGatesAsync(connection, importBatchId, ct).ConfigureAwait(false);
        var duplicateCount = await ReadSuggestedDuplicateCountAsync(connection, importBatchId, ct).ConfigureAwait(false);

        var highConfidenceDuplicateCount = gates
            .First(g => g.Gate == "high_confidence_duplicates")
            .Count;

        await TransitionToWaitingForConfirmationAsync(connection, importBatchId, ct).ConfigureAwait(false);
        var status = await ReadStatusAsync(connection, importBatchId, ct).ConfigureAwait(false);

        return new GedcomReadinessReport(
            ImportBatchId: importBatchId,
            Status: status,
            Gates: gates,
            CanApplyWithoutReview: highConfidenceDuplicateCount == 0,
            RequiresExplicitConfirmation: true,
            DuplicateCount: duplicateCount);
    }

    private static async Task<bool> BatchExistsAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT 1 FROM genealogy.gedcom_import_batch WHERE import_batch_id = @import_batch_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task GenerateDuplicateCandidatesAsync(
        NpgsqlConnection connection, Guid importBatchId, decimal minDuplicateScore, CancellationToken ct)
    {
        const string sql = """
            SELECT genealogy.generate_gedcom_import_duplicate_candidates(@import_batch_id, @min_score);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("min_score", NpgsqlDbType.Numeric) { Value = minDuplicateScore });

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<GedcomReadinessGate>> ReadGatesAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT gate, severity, gate_count
            FROM genealogy.gedcom_import_readiness_gates(@import_batch_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var results = new List<GedcomReadinessGate>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomReadinessGate(
                Gate: reader.GetFieldValue<string>(reader.GetOrdinal("gate")),
                Severity: reader.GetFieldValue<string>(reader.GetOrdinal("severity")),
                Count: reader.GetFieldValue<long>(reader.GetOrdinal("gate_count"))));
        }

        return results;
    }

    private static async Task<long> ReadSuggestedDuplicateCountAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM genealogy.gedcom_import_duplicate_candidate
            WHERE import_batch_id = @import_batch_id
              AND status = 'suggested';
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private static async Task TransitionToWaitingForConfirmationAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            UPDATE genealogy.gedcom_import_batch
            SET status = 'WAITING_FOR_CONFIRMATION',
                previewed_at = COALESCE(previewed_at, now())
            WHERE import_batch_id = @import_batch_id
              AND status IN ('STAGED', 'PREVIEWED');
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadStatusAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT status FROM genealogy.gedcom_import_batch WHERE import_batch_id = @import_batch_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is not string status)
        {
            // The existence check above already guaranteed the row; if it is
            // gone now something concurrent deleted it.
            throw new GedcomImportBatchNotFoundException(importBatchId);
        }

        return status;
    }
}
