using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Thin wrapper around the GEDCOM import APPLY path. Ports the source MCP
/// apply tool's dry-run/real-apply split:
/// <list type="bullet">
/// <item>a dry run never touches <c>genealogy.apply_gedcom_import</c> (migration
/// 0008) — it delegates to <see cref="GedcomImportPreviewService"/> so the
/// caller gets the same change summary a real apply would produce, with zero
/// side effects;</item>
/// <item>a real apply calls <c>genealogy.apply_gedcom_import</c> (migration
/// 0008), which does all of the upsert/delete work and the STAGED/PREVIEWED/
/// WAITING_FOR_CONFIRMATION -&gt; APPLIED status flip itself, inside the
/// function's own execution.</item>
/// </list>
/// This service owns no SQL of its own for the real-apply path beyond invoking
/// the function and translating its exceptions; the function is the single
/// source of truth for what "apply" means (see 0008_gedcom_apply.sql).
/// </summary>
public sealed class GedcomApplyService
{
    private const string DryRunNote = "Dry run only. No GEDCOM changes were applied.";
    private const string AppliedNote = "Applied add/update/upsert changes.";
    private const string AppliedWithDeleteNote =
        "Applied add/update/upsert changes; missing-from-import persons were deleted.";

    private readonly NpgsqlConnectionFactory _connectionFactory;

    public GedcomApplyService(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Applies (or, by default, only previews) a staged GEDCOM import batch.
    /// </summary>
    /// <param name="importBatchId">The batch to apply.</param>
    /// <param name="deleteMissing">
    /// Forwarded verbatim to <c>genealogy.apply_gedcom_import</c>'s
    /// <c>p_delete_missing</c> parameter when <paramref name="dryRun"/> is
    /// false. Ignored in dry-run mode (the preview never deletes anything).
    /// </param>
    /// <param name="dryRun">
    /// Defaults to <c>true</c> — the safe default. When true, no GEDCOM
    /// changes are applied; this delegates to
    /// <see cref="GedcomImportPreviewService.PreviewAsync"/> and returns its
    /// change summary. When false, the batch is actually applied.
    /// </param>
    /// <exception cref="GedcomImportBatchNotFoundException">
    /// No <c>genealogy.gedcom_import_batch</c> row has this id.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="dryRun"/> is false and the batch is not in an
    /// applyable status (must be STAGED, PREVIEWED, or
    /// WAITING_FOR_CONFIRMATION), or <paramref name="deleteMissing"/> is true
    /// and the function's root-present safety valve refused to delete.
    /// </exception>
    public async Task<GedcomApplyResult> ApplyAsync(
        Guid importBatchId,
        bool deleteMissing = false,
        bool dryRun = true,
        CancellationToken ct = default)
    {
        if (dryRun)
        {
            var preview = await new GedcomImportPreviewService(_connectionFactory)
                .PreviewAsync(importBatchId, ct)
                .ConfigureAwait(false);

            var previewChanges = preview.Changes
                .Select(c => new GedcomApplyChange(c.EntityType, c.ChangeType, c.RowCount))
                .ToList();

            return new GedcomApplyResult(
                DryRun: true,
                Status: preview.Batch.Status,
                Changes: previewChanges,
                Note: DryRunNote);
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var changes = await RunApplyFunctionAsync(connection, importBatchId, deleteMissing, ct).ConfigureAwait(false);
        var status = await ReadBatchStatusAsync(connection, importBatchId, ct).ConfigureAwait(false);

        return new GedcomApplyResult(
            DryRun: false,
            Status: status,
            Changes: changes,
            Note: deleteMissing ? AppliedWithDeleteNote : AppliedNote);
    }

    private static async Task<List<GedcomApplyChange>> RunApplyFunctionAsync(
        NpgsqlConnection connection, Guid importBatchId, bool deleteMissing, CancellationToken ct)
    {
        const string sql = """
            SELECT entity_type, change_type, row_count
            FROM genealogy.apply_gedcom_import(@import_batch_id, @delete_missing);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("delete_missing", NpgsqlDbType.Boolean) { Value = deleteMissing });

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var results = new List<GedcomApplyChange>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new GedcomApplyChange(
                    EntityType: reader.GetFieldValue<string>(reader.GetOrdinal("entity_type")),
                    ChangeType: reader.GetFieldValue<string>(reader.GetOrdinal("change_type")),
                    RowCount: reader.GetFieldValue<long>(reader.GetOrdinal("row_count"))));
            }

            return results;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.NoDataFound)
        {
            // genealogy.apply_gedcom_import raises this (errcode =
            // 'no_data_found') when p_import_batch_id has no matching batch
            // row (0008_gedcom_apply.sql, step 0).
            throw new GedcomImportBatchNotFoundException(importBatchId);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.InvalidParameterValue ||
            ex.SqlState == PostgresErrorCodes.RaiseException)
        {
            // InvalidParameterValue ('invalid_parameter_value'): the batch
            // exists but is not in an applyable status (already APPLIED, or
            // CANCELLED). RaiseException ('raise_exception'): the
            // p_delete_missing safety valve refused because the batch's root
            // person is missing from staging. Both carry a clear message from
            // the function itself, so surface it rather than swallowing it.
            throw new InvalidOperationException(ex.MessageText, ex);
        }
    }

    private static async Task<string> ReadBatchStatusAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT status FROM genealogy.gedcom_import_batch WHERE import_batch_id = @import_batch_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string
            ?? throw new InvalidOperationException(
                $"GEDCOM import batch {importBatchId} was applied but its status could not be re-read.");
    }
}
