using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Loads the GEDCOM staging artifacts (11 UTF-16 TSV files +
/// <c>staging_manifest.json</c>) produced by
/// <c>gedcom_tool.py export-staging-tsv</c> into the PostgreSQL
/// <c>genealogy.gedcom_import_*</c> staging tables from migration 0004.
///
/// The batch-header insert and all 11 COPY streams run inside a single
/// transaction: on any failure the whole batch is rolled back, so a partially
/// loaded batch never becomes visible. Bulk rows are streamed with Npgsql
/// binary COPY (<c>COPY ... FROM STDIN (FORMAT BINARY)</c>), the PostgreSQL
/// analogue of the legacy SqlBulkCopy path (plan §6).
/// </summary>
public sealed class GedcomStagingLoader
{
    private const string ManifestFileName = "staging_manifest.json";

    private readonly NpgsqlConnectionFactory _connectionFactory;

    public GedcomStagingLoader(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Reads the manifest from <paramref name="artifactDirectory"/>, inserts the
    /// batch header and streams all 11 TSVs into their staging tables in one
    /// transaction. Rolls back and rethrows on any error.
    /// </summary>
    /// <param name="artifactDirectory">
    /// Directory containing <c>staging_manifest.json</c> and the 11 TSV files.
    /// </param>
    /// <param name="notes">Optional free-text notes stored on the batch header.</param>
    public async Task<GedcomStagingLoadResult> LoadAsync(
        string artifactDirectory,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactDirectory);

        var manifestPath = Path.Combine(artifactDirectory, ManifestFileName);
        var manifest = await GedcomStagingManifest.LoadAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await InsertBatchHeaderAsync(connection, manifest, notes, cancellationToken)
                .ConfigureAwait(false);

            var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in StagingTables)
            {
                var path = Path.Combine(artifactDirectory, table.FileName);
                var loaded = await CopyTsvAsync(connection, path, table, cancellationToken)
                    .ConfigureAwait(false);
                rowCounts[table.TableName] = loaded;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GedcomStagingLoadResult(manifest.BatchId, rowCounts);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task InsertBatchHeaderAsync(
        NpgsqlConnection connection,
        GedcomStagingManifest manifest,
        string? notes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO genealogy.gedcom_import_batch
                (import_batch_id, source_file_path, source_file_hash, tree_id,
                 root_external_id, root_person_id,
                 person_count, family_count, event_count, place_count,
                 scope_invalid_count, status, notes)
            VALUES
                (@import_batch_id, @source_file_path, @source_file_hash, @tree_id,
                 @root_external_id, @root_person_id,
                 @person_count, @family_count, @event_count, @place_count,
                 NULL, 'STAGED', @notes);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = manifest.BatchId });
        command.Parameters.Add(new NpgsqlParameter("source_file_path", NpgsqlDbType.Text) { Value = manifest.SourceFilePath });
        command.Parameters.Add(new NpgsqlParameter("source_file_hash", NpgsqlDbType.Varchar) { Value = (object?)manifest.SourceFileHash ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = manifest.TreeId });
        command.Parameters.Add(new NpgsqlParameter("root_external_id", NpgsqlDbType.Varchar) { Value = (object?)manifest.RootExternalId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("root_person_id", NpgsqlDbType.Uuid) { Value = manifest.RootTreePersonId.HasValue ? manifest.RootTreePersonId.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("person_count", NpgsqlDbType.Integer) { Value = manifest.CountFor("gedcom_import_person.tsv") });
        command.Parameters.Add(new NpgsqlParameter("family_count", NpgsqlDbType.Integer) { Value = manifest.CountFor("gedcom_import_family.tsv") });
        command.Parameters.Add(new NpgsqlParameter("event_count", NpgsqlDbType.Integer) { Value = manifest.CountFor("gedcom_import_event.tsv") });
        command.Parameters.Add(new NpgsqlParameter("place_count", NpgsqlDbType.Integer) { Value = manifest.CountFor("gedcom_import_place.tsv") });
        command.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Text) { Value = (object?)notes ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams one UTF-16 TSV into its staging table via binary COPY. The TSV
    /// header is validated against the expected column list; each data row is
    /// written column-by-column with the column's <see cref="NpgsqlDbType"/>,
    /// empty fields becoming SQL NULL. Returns the number of rows written.
    /// </summary>
    private static async Task<long> CopyTsvAsync(
        NpgsqlConnection connection,
        string path,
        StagingTableSpec table,
        CancellationToken cancellationToken)
    {
        using var streamReader = new StreamReader(
            path, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);

        var headerLine = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"TSV file is empty (no header): {path}");
        var headers = ParseTsvLine(headerLine);
        ValidateHeader(headers, table, path);

        var copySql = table.BuildCopyCommand();
        await using var importer = await connection
            .BeginBinaryImportAsync(copySql, cancellationToken).ConfigureAwait(false);

        long rows = 0;
        string? line;
        while ((line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            var fields = ParseTsvLine(line);
            if (fields.Count != table.Columns.Count)
            {
                throw new InvalidOperationException(
                    $"Unexpected field count in {path}: expected {table.Columns.Count}, got {fields.Count}.");
            }

            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                await WriteFieldAsync(importer, table.Columns[i], fields[i], cancellationToken)
                    .ConfigureAwait(false);
            }

            rows++;
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static void ValidateHeader(
        IReadOnlyList<string> headers, StagingTableSpec table, string path)
    {
        if (headers.Count != table.Columns.Count)
        {
            throw new InvalidOperationException(
                $"Unexpected TSV header column count in {path}: expected {table.Columns.Count}, " +
                $"got {headers.Count} ([{string.Join(", ", headers)}]).");
        }

        for (var i = 0; i < headers.Count; i++)
        {
            if (!string.Equals(headers[i], table.Columns[i].Header, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected TSV header in {path} at position {i}: " +
                    $"expected '{table.Columns[i].Header}', got '{headers[i]}'.");
            }
        }
    }

    private static async Task WriteFieldAsync(
        NpgsqlBinaryImporter importer,
        ColumnSpec column,
        string field,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(field))
        {
            await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (column.Kind)
        {
            case ColumnKind.Text:
                await importer.WriteAsync(field, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                break;
            case ColumnKind.Uuid:
                await importer.WriteAsync(Guid.Parse(field), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                break;
            case ColumnKind.Integer:
                await importer.WriteAsync(int.Parse(field, CultureInfo.InvariantCulture), NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                break;
            case ColumnKind.Smallint:
                await importer.WriteAsync(short.Parse(field, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
                break;
            case ColumnKind.Boolean:
                await importer.WriteAsync(ParseBool(field), NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(false);
                break;
            case ColumnKind.Date:
                await importer.WriteAsync(DateOnly.Parse(field, CultureInfo.InvariantCulture), NpgsqlDbType.Date, cancellationToken).ConfigureAwait(false);
                break;
            case ColumnKind.Numeric:
                await importer.WriteAsync(decimal.Parse(field, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"Unsupported column kind {column.Kind}.");
        }
    }

    /// <summary>
    /// Maps the exporter's boolean encoding: "1"/"true" -> true, "0"/"false" ->
    /// false (empty is handled upstream as NULL).
    /// </summary>
    private static bool ParseBool(string value) => value switch
    {
        "1" or "true" or "True" or "TRUE" => true,
        "0" or "false" or "False" or "FALSE" => false,
        _ => throw new FormatException($"Cannot parse boolean field '{value}'."),
    };

    /// <summary>
    /// Splits one TSV line on tab, honoring the RFC-4180-style double-quote
    /// escaping the exporter can emit (a field wrapped in quotes, with doubled
    /// quotes inside). Mirrors the legacy SQL Server importer's parser.
    /// </summary>
    private static List<string> ParseTsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == '\t' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private enum ColumnKind
    {
        Text,
        Uuid,
        Integer,
        Smallint,
        Boolean,
        Date,
        Numeric,
    }

    /// <summary>
    /// One TSV/target-column binding: the PascalCase TSV header used to validate
    /// the file, the snake_case destination column used in the COPY statement,
    /// and the kind that drives both the <see cref="NpgsqlDbType"/> and the
    /// string-to-value conversion for binary writes.
    /// </summary>
    private sealed record ColumnSpec(string Header, string Column, ColumnKind Kind);

    private sealed record StagingTableSpec(
        string FileName, string TableName, IReadOnlyList<ColumnSpec> Columns)
    {
        public string BuildCopyCommand()
        {
            var columns = string.Join(", ", Columns.Select(c => c.Column));
            return $"COPY {TableName} ({columns}) FROM STDIN (FORMAT BINARY)";
        }
    }

    private static ColumnSpec Text(string header, string column) => new(header, column, ColumnKind.Text);
    private static ColumnSpec Uuid(string header, string column) => new(header, column, ColumnKind.Uuid);
    private static ColumnSpec Int(string header, string column) => new(header, column, ColumnKind.Integer);
    private static ColumnSpec Small(string header, string column) => new(header, column, ColumnKind.Smallint);
    private static ColumnSpec Bool(string header, string column) => new(header, column, ColumnKind.Boolean);
    private static ColumnSpec Date(string header, string column) => new(header, column, ColumnKind.Date);
    private static ColumnSpec Numeric(string header, string column) => new(header, column, ColumnKind.Numeric);

    /// <summary>
    /// The 11 staging tables in a foreign-key-safe load order (person before its
    /// parsed names; event before its citations and date warnings). Column order
    /// per table exactly matches the TSV header emitted by the exporter and the
    /// column list in migration 0004. <c>created_at</c> on the parsed-name table
    /// is intentionally omitted — it is not in the TSV and the DB default fills it.
    /// </summary>
    private static readonly StagingTableSpec[] StagingTables =
    [
        new("gedcom_import_person.tsv", "genealogy.gedcom_import_person",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Uuid("TreePersonId", "tree_person_id"),
            Text("ExternalId", "external_id"),
            Text("Sex", "sex"),
            Bool("IsLiving", "is_living"),
            Text("PrimaryDisplayName", "primary_display_name"),
            Text("SurnameNormalized", "surname_normalized"),
        ]),
        new("gedcom_import_person_name.tsv", "genealogy.gedcom_import_person_name",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Int("RowNumber", "row_number"),
            Uuid("TreePersonId", "tree_person_id"),
            Text("ScriptCode", "script_code"),
            Text("NameType", "name_type"),
            Text("Given", "given"),
            Text("Surname", "surname"),
            Text("FullName", "full_name"),
            Text("FullNameNormalized", "full_name_normalized"),
            Bool("IsPrimary", "is_primary"),
        ]),
        new("gedcom_import_person_name_parsed.tsv", "genealogy.gedcom_import_person_name_parsed",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Int("RowNumber", "row_number"),
            Int("SourceNameRowNumber", "source_name_row_number"),
            Uuid("TreePersonId", "tree_person_id"),
            Text("RawName", "raw_name"),
            Text("NameType", "name_type"),
            Text("ScriptCode", "script_code"),
            Text("GivenName", "given_name"),
            Text("Patronymic", "patronymic"),
            Text("Surname", "surname"),
            Text("MaidenSurname", "maiden_surname"),
            Text("MarriedSurname", "married_surname"),
            Text("TitlePrefix", "title_prefix"),
            Text("Suffix", "suffix"),
            Text("LanguageHint", "language_hint"),
            Text("GivenNameNormalized", "given_name_normalized"),
            Text("PatronymicNormalized", "patronymic_normalized"),
            Text("SurnameNormalized", "surname_normalized"),
            Text("FullNameNormalized", "full_name_normalized"),
            Text("NameTokens", "name_tokens"),
            Text("VariantExplanation", "variant_explanation"),
            Numeric("NormalizationConfidence", "normalization_confidence"),
            Text("ParserStatus", "parser_status"),
        ]),
        new("gedcom_import_place.tsv", "genealogy.gedcom_import_place",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Int("RowNumber", "row_number"),
            Text("PlaceRaw", "place_raw"),
            Text("PlaceNormalized", "place_normalized"),
        ]),
        new("gedcom_import_family.tsv", "genealogy.gedcom_import_family",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Uuid("FamilyId", "family_id"),
            Text("ExternalId", "external_id"),
            Uuid("Spouse1TreePersonId", "spouse1_tree_person_id"),
            Uuid("Spouse2TreePersonId", "spouse2_tree_person_id"),
            Text("MarriageDateRaw", "marriage_date_raw"),
            Small("MarriageYear", "marriage_year"),
            Text("MarriagePlaceRaw", "marriage_place_raw"),
            Text("Notes", "notes"),
        ]),
        new("gedcom_import_family_child.tsv", "genealogy.gedcom_import_family_child",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Uuid("FamilyId", "family_id"),
            Uuid("ChildTreePersonId", "child_tree_person_id"),
        ]),
        new("gedcom_import_parent_of.tsv", "genealogy.gedcom_import_parent_of",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Uuid("ParentTreePersonId", "parent_tree_person_id"),
            Uuid("ChildTreePersonId", "child_tree_person_id"),
            Text("RelationType", "relation_type"),
        ]),
        new("gedcom_import_spouse_of.tsv", "genealogy.gedcom_import_spouse_of",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Int("RowNumber", "row_number"),
            Uuid("FromTreePersonId", "from_tree_person_id"),
            Uuid("ToTreePersonId", "to_tree_person_id"),
            Text("RelationType", "relation_type"),
            Uuid("FamilyId", "family_id"),
            Small("MarriageYear", "marriage_year"),
            Text("MarriagePlaceRaw", "marriage_place_raw"),
        ]),
        new("gedcom_import_event.tsv", "genealogy.gedcom_import_event",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Int("RowNumber", "row_number"),
            Text("ExternalEventKey", "external_event_key"),
            Uuid("TreePersonId", "tree_person_id"),
            Text("EventType", "event_type"),
            Text("EventValue", "event_value"),
            Text("DateRaw", "date_raw"),
            Date("DateFrom", "date_from"),
            Date("DateTo", "date_to"),
            Small("YearFrom", "year_from"),
            Small("YearTo", "year_to"),
            Text("PlaceRaw", "place_raw"),
            Text("PlaceNormalized", "place_normalized"),
            Uuid("FamilyId", "family_id"),
            Uuid("RelatedTreePersonId", "related_tree_person_id"),
            Bool("IsDerived", "is_derived"),
            Text("Notes", "notes"),
        ]),
        new("date_parse_warnings.tsv", "genealogy.gedcom_import_date_warning",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Int("EventRowNumber", "event_row_number"),
            Uuid("TreePersonId", "tree_person_id"),
            Text("PersonXref", "person_xref"),
            Text("PersonName", "person_name"),
            Text("EventType", "event_type"),
            Text("DateRaw", "date_raw"),
            Date("DateFrom", "date_from"),
            Date("DateTo", "date_to"),
            Text("DatePrecision", "date_precision"),
            Text("DateModifier", "date_modifier"),
            Text("DateStatus", "date_status"),
            Text("WarningKind", "warning_kind"),
            Text("WarningMessage", "warning_message"),
        ]),
        new("gedcom_import_event_citation.tsv", "genealogy.gedcom_import_event_citation",
        [
            Uuid("ImportBatchId", "import_batch_id"),
            Int("RowNumber", "row_number"),
            Int("EventRowNumber", "event_row_number"),
            Text("SourceRef", "source_ref"),
            Text("SourceTitle", "source_title"),
            Text("Page", "page"),
            Text("Quality", "quality"),
            Text("CitationDateRaw", "citation_date_raw"),
            Text("CitationText", "citation_text"),
            Text("Note", "note"),
        ]),
    ];
}

/// <summary>
/// Outcome of <see cref="GedcomStagingLoader.LoadAsync"/>: the batch that was
/// staged and the number of rows loaded into each staging table (keyed by fully
/// qualified table name).
/// </summary>
public sealed record GedcomStagingLoadResult(
    Guid BatchId,
    IReadOnlyDictionary<string, long> RowCounts);
