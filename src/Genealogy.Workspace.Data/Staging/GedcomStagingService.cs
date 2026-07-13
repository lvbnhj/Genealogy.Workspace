using System.Diagnostics;
using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Owns the GEDCOM import batch LIFECYCLE: stage (parse a GEDCOM with
/// <c>gedcom_tool.py export-staging-tsv</c> and load the artifacts via
/// <see cref="GedcomStagingLoader"/>), list-pending, and cancel. Preview
/// (<see cref="GedcomImportPreviewService"/>) and apply are separate services.
///
/// The python tool is invoked as a plain subprocess with no database
/// credentials: it only ever produces deterministic, database-neutral TSV +
/// JSON artifacts on disk (plan §10 Phase 3). All database access — the
/// staging load, the pending-list query, and the cancel update — happens here
/// in .NET via parameterized SQL, mirroring <see cref="Repositories.TreeRepository"/>.
/// </summary>
public sealed class GedcomStagingService
{
    private const string ExportStagingTsvCommand = "export-staging-tsv";

    private readonly NpgsqlConnectionFactory _connectionFactory;

    public GedcomStagingService(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Runs <c>gedcom_tool.py export-staging-tsv</c> against
    /// <paramref name="request"/>'s GEDCOM file and loads the resulting
    /// artifacts into the <c>genealogy.gedcom_import_*</c> staging tables via
    /// <see cref="GedcomStagingLoader"/>. Does not preview or apply the batch.
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="GedcomStageRequest.GedcomFilePath"/> is blank.</exception>
    /// <exception cref="FileNotFoundException">The GEDCOM file or <c>gedcom_tool.py</c> does not exist.</exception>
    /// <exception cref="InvalidOperationException">The python process failed to start or exited non-zero.</exception>
    public async Task<GedcomStagingLoadResult> StageAsync(
        GedcomStageRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.GedcomFilePath))
        {
            throw new ArgumentException("GedcomFilePath is required.", nameof(request));
        }

        var gedcomPath = Path.GetFullPath(request.GedcomFilePath);
        if (!File.Exists(gedcomPath))
        {
            throw new FileNotFoundException($"GEDCOM file not found: {gedcomPath}", gedcomPath);
        }

        var toolPath = request.GedcomToolPath is null
            ? ResolveDefaultGedcomToolPath()
            : Path.GetFullPath(request.GedcomToolPath);
        if (!File.Exists(toolPath))
        {
            throw new FileNotFoundException($"gedcom_tool.py not found: {toolPath}", toolPath);
        }

        var outputDirectory = request.OutputDirectory is null
            ? CreateFreshTempDirectory()
            : Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        await RunGedcomToolAsync(request, gedcomPath, toolPath, outputDirectory, ct).ConfigureAwait(false);

        var loader = new GedcomStagingLoader(_connectionFactory);
        return await loader.LoadAsync(outputDirectory, request.Notes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists import batches awaiting review: <c>STAGED</c> and
    /// <c>WAITING_FOR_CONFIRMATION</c> always, plus <c>PREVIEWED</c> when
    /// <paramref name="includePreviewed"/> is true. Mirrors
    /// <c>ged.ListPendingGedcomImports</c> minus the duplicate-candidate
    /// counts (Phase 4).
    /// </summary>
    /// <param name="treeId">When set, restricts results to this tree.</param>
    /// <param name="includePreviewed">Include <c>PREVIEWED</c> batches (default true, matching the source procedure's default).</param>
    /// <param name="topN">Maximum rows to return; clamped to [1, 500].</param>
    public async Task<IReadOnlyList<GedcomPendingImport>> ListPendingAsync(
        Guid? treeId = null,
        bool includePreviewed = true,
        int topN = 50,
        CancellationToken ct = default)
    {
        var clampedTopN = Math.Clamp(topN, 1, 500);

        const string sql = """
            SELECT
                b.import_batch_id,
                b.tree_id,
                t.name AS tree_name,
                b.root_external_id,
                b.root_person_id,
                root.primary_display_name AS root_name,
                b.source_file_path,
                b.source_file_hash,
                b.person_count,
                b.family_count,
                b.event_count,
                b.place_count,
                b.scope_invalid_count,
                b.status,
                b.created_at,
                b.previewed_at,
                b.applied_at,
                b.cancelled_at,
                b.notes,
                (
                    SELECT count(*)
                    FROM genealogy.gedcom_import_person_name_parsed p
                    WHERE p.import_batch_id = b.import_batch_id
                      AND p.parser_status <> 'OK'
                ) AS name_issue_count,
                (
                    SELECT count(*)
                    FROM genealogy.gedcom_import_date_warning w
                    WHERE w.import_batch_id = b.import_batch_id
                ) AS date_warning_count
            FROM genealogy.gedcom_import_batch b
            JOIN genealogy.tree t ON t.tree_id = b.tree_id
            LEFT JOIN genealogy.person root
              ON root.tree_id = b.tree_id
             AND root.person_id = b.root_person_id
            WHERE (@tree_id IS NULL OR b.tree_id = @tree_id)
              AND (
                    b.status IN ('STAGED', 'WAITING_FOR_CONFIRMATION')
                 OR (@include_previewed AND b.status = 'PREVIEWED')
              )
            ORDER BY b.created_at DESC
            LIMIT @top_n;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = (object?)treeId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("include_previewed", NpgsqlDbType.Boolean) { Value = includePreviewed });
        command.Parameters.Add(new NpgsqlParameter("top_n", NpgsqlDbType.Integer) { Value = clampedTopN });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var results = new List<GedcomPendingImport>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapPendingImport(reader));
        }

        return results;
    }

    /// <summary>
    /// Cancels a pending import batch. Idempotent: cancelling an
    /// already-cancelled batch is a no-op that returns the current header
    /// unchanged (the <c>WHERE status &lt;&gt; 'CANCELLED'</c> guard on the
    /// update). Never deletes staging rows — only the batch header's status
    /// and timestamps change. Mirrors <c>ged.CancelGedcomImport</c>.
    /// </summary>
    /// <exception cref="GedcomImportBatchNotFoundException">No batch with this id exists.</exception>
    /// <exception cref="GedcomImportBatchAlreadyAppliedException">The batch has already been applied.</exception>
    public async Task<GedcomCancelResult> CancelAsync(
        Guid importBatchId,
        string? reason = null,
        CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var status = await ReadStatusAsync(connection, importBatchId, ct).ConfigureAwait(false);
        if (status is null)
        {
            throw new GedcomImportBatchNotFoundException(importBatchId);
        }

        if (string.Equals(status, "APPLIED", StringComparison.Ordinal))
        {
            throw new GedcomImportBatchAlreadyAppliedException(importBatchId);
        }

        await UpdateCancelledAsync(connection, importBatchId, reason, ct).ConfigureAwait(false);

        return await ReadCancelResultAsync(connection, importBatchId, ct).ConfigureAwait(false)
            ?? throw new GedcomImportBatchNotFoundException(importBatchId);
    }

    private static async Task RunGedcomToolAsync(
        GedcomStageRequest request,
        string gedcomPath,
        string toolPath,
        string outputDirectory,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.PythonExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Directory.GetCurrentDirectory(),
        };

        startInfo.ArgumentList.Add(toolPath);
        startInfo.ArgumentList.Add(ExportStagingTsvCommand);
        startInfo.ArgumentList.Add(gedcomPath);
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--tree-id");
        startInfo.ArgumentList.Add(request.TreeId.ToString());

        if (!string.IsNullOrWhiteSpace(request.TreeName))
        {
            startInfo.ArgumentList.Add("--tree-name");
            startInfo.ArgumentList.Add(request.TreeName);
        }

        if (request.LegacyIds)
        {
            startInfo.ArgumentList.Add("--legacy-ids");
        }

        if (!string.IsNullOrWhiteSpace(request.RootXref))
        {
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(request.RootXref);
        }

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            startInfo.ArgumentList.Add("--notes");
            startInfo.ArgumentList.Add(request.Notes);
        }

        if (request.BatchId.HasValue)
        {
            startInfo.ArgumentList.Add("--batch-id");
            startInfo.ArgumentList.Add(request.BatchId.Value.ToString());
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start '{request.PythonExecutable} {toolPath}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gedcom_tool.py {ExportStagingTsvCommand} exited with code {process.ExitCode}." +
                $"{Environment.NewLine}stdout:{Environment.NewLine}{stdout}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }
    }

    /// <summary>
    /// Walks up from the running assembly's base directory looking for
    /// <c>tools/gedcom/gedcom_tool.py</c>. Works both when the repository root
    /// IS the workspace (standalone: <c>&lt;root&gt;/tools/gedcom/…</c>, regardless of
    /// the folder's name) and when the workspace is nested under a parent
    /// (monorepo: <c>&lt;parent&gt;/Genealogy.Workspace/tools/gedcom/…</c>), mirroring
    /// <c>MigrationEngine</c>'s discovery. Pass
    /// <c>GedcomStageRequest.GedcomToolPath</c> to override.
    /// </summary>
    private static string ResolveDefaultGedcomToolPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // Standalone / running from within the workspace (folder name irrelevant).
            var here = Path.Combine(dir.FullName, "tools", "gedcom", "gedcom_tool.py");
            if (File.Exists(here))
            {
                return here;
            }

            // Monorepo: a parent directory contains Genealogy.Workspace/tools/gedcom/…
            var nested = Path.Combine(dir.FullName, "Genealogy.Workspace", "tools", "gedcom", "gedcom_tool.py");
            if (File.Exists(nested))
            {
                return nested;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate tools/gedcom/gedcom_tool.py by walking up from " +
            $"{AppContext.BaseDirectory}. Pass GedcomStageRequest.GedcomToolPath explicitly.");
    }

    private static string CreateFreshTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gedcom-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string?> ReadStatusAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT status FROM genealogy.gedcom_import_batch WHERE import_batch_id = @import_batch_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    /// <summary>
    /// Idempotent cancel: only rows not already <c>CANCELLED</c> are touched.
    /// The notes append mirrors the source procedure's
    /// <c>CONCAT_WS(CHAR(10), Notes, CASE WHEN NULLIF(LTRIM(RTRIM(@Reason)), N'')
    /// IS NOT NULL THEN CONCAT(N'Cancelled: ', @Reason) END)</c> using the
    /// PostgreSQL equivalents (<c>concat_ws</c>/<c>nullif</c>/<c>btrim</c> all
    /// exist under the same names and null-handling semantics).
    /// </summary>
    private static async Task UpdateCancelledAsync(
        NpgsqlConnection connection, Guid importBatchId, string? reason, CancellationToken ct)
    {
        const string sql = """
            UPDATE genealogy.gedcom_import_batch
            SET status = 'CANCELLED',
                cancelled_at = now(),
                notes = concat_ws(chr(10), notes,
                    CASE WHEN nullif(btrim(@reason), '') IS NOT NULL
                         THEN concat('Cancelled: ', @reason)
                    END)
            WHERE import_batch_id = @import_batch_id
              AND status <> 'CANCELLED';
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<GedcomCancelResult?> ReadCancelResultAsync(
        NpgsqlConnection connection, Guid importBatchId, CancellationToken ct)
    {
        const string sql = """
            SELECT import_batch_id, tree_id, status, created_at, previewed_at, applied_at, cancelled_at, notes
            FROM genealogy.gedcom_import_batch
            WHERE import_batch_id = @import_batch_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new GedcomCancelResult(
            ImportBatchId: reader.GetFieldValue<Guid>(reader.GetOrdinal("import_batch_id")),
            TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            PreviewedAt: reader.GetNullableValue<DateTimeOffset>("previewed_at"),
            AppliedAt: reader.GetNullableValue<DateTimeOffset>("applied_at"),
            CancelledAt: reader.GetNullableValue<DateTimeOffset>("cancelled_at"),
            Notes: reader.GetNullableString("notes"));
    }

    private static GedcomPendingImport MapPendingImport(NpgsqlDataReader reader) =>
        new(
            ImportBatchId: reader.GetFieldValue<Guid>(reader.GetOrdinal("import_batch_id")),
            TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
            TreeName: reader.GetFieldValue<string>(reader.GetOrdinal("tree_name")),
            RootExternalId: reader.GetNullableString("root_external_id"),
            RootPersonId: reader.GetNullableValue<Guid>("root_person_id"),
            RootName: reader.GetNullableString("root_name"),
            SourceFilePath: reader.GetFieldValue<string>(reader.GetOrdinal("source_file_path")),
            SourceFileHash: reader.GetNullableString("source_file_hash"),
            PersonCount: reader.GetNullableValue<int>("person_count"),
            FamilyCount: reader.GetNullableValue<int>("family_count"),
            EventCount: reader.GetNullableValue<int>("event_count"),
            PlaceCount: reader.GetNullableValue<int>("place_count"),
            ScopeInvalidCount: reader.GetNullableValue<int>("scope_invalid_count"),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            PreviewedAt: reader.GetNullableValue<DateTimeOffset>("previewed_at"),
            AppliedAt: reader.GetNullableValue<DateTimeOffset>("applied_at"),
            CancelledAt: reader.GetNullableValue<DateTimeOffset>("cancelled_at"),
            Notes: reader.GetNullableString("notes"),
            NameIssueCount: reader.GetFieldValue<long>(reader.GetOrdinal("name_issue_count")),
            DateWarningCount: reader.GetFieldValue<long>(reader.GetOrdinal("date_warning_count")));
}
