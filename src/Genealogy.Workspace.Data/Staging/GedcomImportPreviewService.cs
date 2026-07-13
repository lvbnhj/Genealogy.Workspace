using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Ports the GEDCOM import PREVIEW logic from the SQL Server procedure
/// <c>ged.GetGedcomImportPreview</c>
/// (Database/Procedures/ged/GetGedcomImportPreview.sql). It diffs a staged
/// import batch (<c>genealogy.gedcom_import_*</c>, migration 0004) against the
/// production tree (<c>genealogy.*</c>, migration 0002) and returns the three
/// result parts the source produces.
///
/// The heavy change-classification SQL lives in two versioned PostgreSQL
/// functions added by migration 0005
/// (<c>genealogy.gedcom_import_preview_counts</c> and
/// <c>genealogy.gedcom_import_preview_person_samples</c>) per the plan's
/// allowance for complex reads to live in SQL functions
/// (docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §3). This service owns the
/// side effect (the STAGED -> PREVIEWED status flip) and the batch-header read,
/// keeping those SQL functions read-only.
/// </summary>
public sealed class GedcomImportPreviewService
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public GedcomImportPreviewService(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Previews a staged import batch. Mirrors the source procedure:
    /// <list type="number">
    /// <item>throws if the batch does not exist (source lines 7-8);</item>
    /// <item>flips <c>STAGED</c> -> <c>PREVIEWED</c> and stamps
    /// <c>previewed_at = now()</c> (source lines 15-18);</item>
    /// <item>returns the batch header, the per-entity change counts, and a
    /// capped person-change sample (source result sets 1-3).</item>
    /// </list>
    /// </summary>
    /// <exception cref="GedcomImportBatchNotFoundException">
    /// No <c>genealogy.gedcom_import_batch</c> row has this id.
    /// </exception>
    public async Task<GedcomImportPreviewResult> PreviewAsync(
        Guid importBatchId,
        CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Source lines 7-8: existence check up front so a missing batch throws
        // before the status flip (an UPDATE against a nonexistent id would
        // silently affect zero rows).
        if (!await BatchExistsAsync(connection, importBatchId, ct).ConfigureAwait(false))
        {
            throw new GedcomImportBatchNotFoundException(importBatchId);
        }

        // Source lines 15-18: idempotent status flip. Only STAGED advances to
        // PREVIEWED; every other status is left unchanged. previewed_at is set
        // to now() unconditionally, exactly as the source proc sets PreviewedAt
        // = sysutcdatetime() regardless of the prior status.
        await FlipStatusAsync(connection, importBatchId, ct).ConfigureAwait(false);

        var batch = await ReadBatchHeaderAsync(connection, importBatchId, ct).ConfigureAwait(false);
        var changes = await ReadChangesAsync(connection, importBatchId, ct).ConfigureAwait(false);
        var samples = await ReadPersonSamplesAsync(connection, importBatchId, ct).ConfigureAwait(false);

        return new GedcomImportPreviewResult(batch, changes, samples);
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

    private static async Task FlipStatusAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            UPDATE genealogy.gedcom_import_batch
            SET status = CASE WHEN status = 'STAGED' THEN 'PREVIEWED' ELSE status END,
                previewed_at = now()
            WHERE import_batch_id = @import_batch_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<GedcomImportPreviewBatch> ReadBatchHeaderAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        // Source lines 20-39. TreeName via subquery; RootTreePersonId column
        // maps to workspace root_person_id.
        const string sql = """
            SELECT
                b.import_batch_id,
                b.tree_id,
                (SELECT t.name FROM genealogy.tree t WHERE t.tree_id = b.tree_id) AS tree_name,
                b.source_file_path,
                b.source_file_hash,
                b.root_external_id,
                b.root_person_id,
                b.person_count,
                b.family_count,
                b.event_count,
                b.place_count,
                b.scope_invalid_count,
                b.status,
                b.created_at,
                b.previewed_at,
                b.applied_at,
                b.notes
            FROM genealogy.gedcom_import_batch b
            WHERE b.import_batch_id = @import_batch_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // The existence check above already guaranteed the row; if it is
            // gone now something concurrent deleted it.
            throw new GedcomImportBatchNotFoundException(importBatchId);
        }

        return new GedcomImportPreviewBatch(
            ImportBatchId: reader.GetFieldValue<Guid>(reader.GetOrdinal("import_batch_id")),
            TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
            TreeName: reader.GetNullableString("tree_name"),
            SourceFilePath: reader.GetFieldValue<string>(reader.GetOrdinal("source_file_path")),
            SourceFileHash: reader.GetNullableString("source_file_hash"),
            RootExternalId: reader.GetNullableString("root_external_id"),
            RootPersonId: reader.GetNullableValue<Guid>("root_person_id"),
            PersonCount: reader.GetNullableValue<int>("person_count"),
            FamilyCount: reader.GetNullableValue<int>("family_count"),
            EventCount: reader.GetNullableValue<int>("event_count"),
            PlaceCount: reader.GetNullableValue<int>("place_count"),
            ScopeInvalidCount: reader.GetNullableValue<int>("scope_invalid_count"),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            PreviewedAt: reader.GetNullableValue<DateTimeOffset>("previewed_at"),
            AppliedAt: reader.GetNullableValue<DateTimeOffset>("applied_at"),
            Notes: reader.GetNullableString("notes"));
    }

    private static async Task<IReadOnlyList<GedcomImportPreviewChange>> ReadChangesAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        // Source result set 2 (source lines 41-368). All classification logic,
        // including the >0 filter and ORDER BY, lives in the SQL function.
        const string sql = """
            SELECT entity_type, change_type, row_count
            FROM genealogy.gedcom_import_preview_counts(@import_batch_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var results = new List<GedcomImportPreviewChange>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomImportPreviewChange(
                EntityType: reader.GetFieldValue<string>(reader.GetOrdinal("entity_type")),
                ChangeType: reader.GetFieldValue<string>(reader.GetOrdinal("change_type")),
                RowCount: reader.GetFieldValue<long>(reader.GetOrdinal("row_count"))));
        }

        return results;
    }

    private static async Task<IReadOnlyList<GedcomImportPreviewPersonChange>> ReadPersonSamplesAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        // Source result set 3 (source lines 370-390): TOP 200 person ADD/UPDATE
        // rows. The LIMIT 200 and ORDER BY live in the SQL function.
        const string sql = """
            SELECT entity_type, change_type, tree_person_id, external_id,
                   primary_display_name, current_primary_display_name, sex, current_sex
            FROM genealogy.gedcom_import_preview_person_samples(@import_batch_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var results = new List<GedcomImportPreviewPersonChange>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new GedcomImportPreviewPersonChange(
                EntityType: reader.GetFieldValue<string>(reader.GetOrdinal("entity_type")),
                ChangeType: reader.GetFieldValue<string>(reader.GetOrdinal("change_type")),
                TreePersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_person_id")),
                ExternalId: reader.GetNullableString("external_id"),
                PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
                CurrentPrimaryDisplayName: reader.GetNullableString("current_primary_display_name"),
                Sex: reader.GetNullableChar("sex"),
                CurrentSex: reader.GetNullableChar("current_sex")));
        }

        return results;
    }
}

/// <summary>
/// Thrown when <see cref="GedcomImportPreviewService.PreviewAsync"/> is asked
/// to preview an import batch id that has no
/// <c>genealogy.gedcom_import_batch</c> row. Mirrors the source procedure's
/// <c>THROW 53001, 'GEDCOM import batch not found.', 1</c> (source line 8).
/// </summary>
public sealed class GedcomImportBatchNotFoundException : Exception
{
    public GedcomImportBatchNotFoundException(Guid importBatchId)
        : base($"GEDCOM import batch not found: {importBatchId}.")
    {
        ImportBatchId = importBatchId;
    }

    public Guid ImportBatchId { get; }
}
