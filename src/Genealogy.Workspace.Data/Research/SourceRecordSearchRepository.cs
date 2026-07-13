using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Evidence Inbox search over <c>research.source_record</c> (plan §8 "MVP
/// search filters"). Every optional filter is expressed as
/// <c>(@param IS NULL OR ...)</c> — mirroring
/// <see cref="Repositories.PersonSearchRepository"/> — so an absent filter
/// never restricts the result set. Parameterized SQL only.
/// </summary>
public sealed class SourceRecordSearchRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public SourceRecordSearchRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<SourceRecordSearchResults> SearchAsync(
        ResearchSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var effectiveTopN = Math.Clamp(query.TopN <= 0 ? 50 : query.TopN, 1, 500);

        // count(*) over () computes the total matching row count (pre-LIMIT)
        // in the same pass as the page of results, avoiding a second query.
        const string sql = """
            SELECT
                sr.source_record_id, sr.title, sr.record_type, sr.status,
                sr.record_year_from, sr.record_year_to, sr.place_text, sr.church_text,
                sr.archive_name, sr.fond, sr.opis, sr.sprava, sr.page,
                sr.created_at, sr.updated_at,
                count(*) OVER () AS total_count
            FROM research.source_record sr
            WHERE sr.tree_id = @tree_id

              AND (@status IS NULL OR sr.status = @status)
              AND (@record_type IS NULL OR sr.record_type = @record_type)

              AND (@free_text_pattern IS NULL OR (
                    sr.title ILIKE @free_text_pattern ESCAPE '\'
                    OR sr.record_text ILIKE @free_text_pattern ESCAPE '\'
                    OR sr.transcription ILIKE @free_text_pattern ESCAPE '\'
                  ))

              AND (@surname_pattern IS NULL OR EXISTS (
                    SELECT 1 FROM research.record_person_mention rpm
                    WHERE rpm.source_record_id = sr.source_record_id
                      AND rpm.surname ILIKE @surname_pattern ESCAPE '\'
                  ))

              AND (@given_name_pattern IS NULL OR EXISTS (
                    SELECT 1 FROM research.record_person_mention rpm
                    WHERE rpm.source_record_id = sr.source_record_id
                      AND rpm.given_name ILIKE @given_name_pattern ESCAPE '\'
                  ))

              AND (@place_pattern IS NULL OR (
                    sr.place_text ILIKE @place_pattern ESCAPE '\'
                    OR sr.church_text ILIKE @place_pattern ESCAPE '\'
                    OR EXISTS (
                        SELECT 1 FROM research.record_place_mention rpm
                        WHERE rpm.source_record_id = sr.source_record_id
                          AND (rpm.place_text ILIKE @place_pattern ESCAPE '\'
                               OR rpm.normalized_name ILIKE @place_pattern ESCAPE '\')
                    )
                  ))

              AND (@archive_name_pattern IS NULL OR sr.archive_name ILIKE @archive_name_pattern ESCAPE '\')
              AND (@fond_pattern IS NULL OR sr.fond ILIKE @fond_pattern ESCAPE '\')
              AND (@opis_pattern IS NULL OR sr.opis ILIKE @opis_pattern ESCAPE '\')
              AND (@sprava_pattern IS NULL OR sr.sprava ILIKE @sprava_pattern ESCAPE '\')
              AND (@page_pattern IS NULL OR sr.page ILIKE @page_pattern ESCAPE '\')

              -- Year-range overlap: the record's own year span vs the query's.
              AND (@year_from IS NULL OR sr.record_year_to >= @year_from)
              AND (@year_to IS NULL OR sr.record_year_from <= @year_to)

              AND (@keyword_pattern IS NULL OR EXISTS (
                    SELECT 1 FROM research.source_record_keyword srk
                    WHERE srk.source_record_id = sr.source_record_id
                      AND srk.keyword ILIKE @keyword_pattern ESCAPE '\'
                  ))

              AND (@role IS NULL OR EXISTS (
                    SELECT 1 FROM research.record_person_mention rpm
                    WHERE rpm.source_record_id = sr.source_record_id
                      AND rpm.role = @role
                  ))

              -- Linked tri-state: null = no filter; true = at least one accepted
              -- mention; false = no accepted mention at all.
              AND (
                    @linked IS NULL
                    OR (@linked = TRUE AND EXISTS (
                          SELECT 1 FROM research.record_person_mention rpm
                          WHERE rpm.source_record_id = sr.source_record_id AND rpm.status = 'accepted'
                        ))
                    OR (@linked = FALSE AND NOT EXISTS (
                          SELECT 1 FROM research.record_person_mention rpm
                          WHERE rpm.source_record_id = sr.source_record_id AND rpm.status = 'accepted'
                        ))
                  )

            ORDER BY sr.created_at DESC, sr.source_record_id
            LIMIT @top_n;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = query.TreeId });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)NullIfBlank(query.Status) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_type", NpgsqlDbType.Text) { Value = (object?)NullIfBlank(query.RecordType) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("free_text_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.FreeText, PatternKind.Contains) });
        command.Parameters.Add(new NpgsqlParameter("surname_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.Surname, PatternKind.Contains) });
        command.Parameters.Add(new NpgsqlParameter("given_name_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.GivenName, PatternKind.Contains) });
        command.Parameters.Add(new NpgsqlParameter("place_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.Place, PatternKind.Contains) });
        command.Parameters.Add(new NpgsqlParameter("archive_name_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.ArchiveName, PatternKind.Prefix) });
        command.Parameters.Add(new NpgsqlParameter("fond_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.Fond, PatternKind.Prefix) });
        command.Parameters.Add(new NpgsqlParameter("opis_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.Opis, PatternKind.Prefix) });
        command.Parameters.Add(new NpgsqlParameter("sprava_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.Sprava, PatternKind.Prefix) });
        command.Parameters.Add(new NpgsqlParameter("page_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.Page, PatternKind.Prefix) });
        command.Parameters.Add(new NpgsqlParameter("year_from", NpgsqlDbType.Smallint) { Value = (object?)query.YearFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("year_to", NpgsqlDbType.Smallint) { Value = (object?)query.YearTo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("keyword_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(query.Keyword, PatternKind.Contains) });
        command.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Text) { Value = (object?)NullIfBlank(query.Role) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("linked", NpgsqlDbType.Boolean) { Value = (object?)query.Linked ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("top_n", NpgsqlDbType.Integer) { Value = effectiveTopN });

        var records = new List<SourceRecordSearchItem>();
        var totalCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new SourceRecordSearchItem(
                SourceRecordId: reader.GetFieldValue<Guid>(reader.GetOrdinal("source_record_id")),
                Title: reader.GetFieldValue<string>(reader.GetOrdinal("title")),
                RecordType: reader.GetFieldValue<string>(reader.GetOrdinal("record_type")),
                Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
                RecordYearFrom: reader.GetNullableValue<short>("record_year_from"),
                RecordYearTo: reader.GetNullableValue<short>("record_year_to"),
                PlaceText: reader.GetNullableString("place_text"),
                ChurchText: reader.GetNullableString("church_text"),
                ArchiveName: reader.GetNullableString("archive_name"),
                Fond: reader.GetNullableString("fond"),
                Opis: reader.GetNullableString("opis"),
                Sprava: reader.GetNullableString("sprava"),
                Page: reader.GetNullableString("page"),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                UpdatedAt: reader.GetNullableValue<DateTimeOffset>("updated_at")));

            var count = reader.GetFieldValue<long>(reader.GetOrdinal("total_count"));
            totalCount = count > int.MaxValue ? int.MaxValue : (int)count;
        }

        return new SourceRecordSearchResults(records, totalCount);
    }

    private enum PatternKind
    {
        Contains,
        Prefix,
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object ToPatternOrNull(string? value, PatternKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DBNull.Value;
        }

        var escaped = EscapeLikePattern(value.Trim());
        return kind == PatternKind.Prefix ? escaped + "%" : "%" + escaped + "%";
    }

    /// <summary>Escapes LIKE/ILIKE metacharacters so user input is matched literally.</summary>
    private static string EscapeLikePattern(string input) =>
        input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
}
