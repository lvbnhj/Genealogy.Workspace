using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Create/read/update access to <c>research.source_record</c> plus its
/// keywords, mentions and link candidates (migration
/// 0009_research_evidence_schema.sql). Evidence Inbox root entity — see
/// docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §8/§9, Phase 6. App-generated
/// uuids, parameterized SQL only.
/// </summary>
public sealed class SourceRecordRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public SourceRecordRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Inserts a new source record with an app-generated uuid. When
    /// <paramref name="input"/>.Status is null the row takes the schema
    /// default (<c>inbox</c>).
    /// </summary>
    public async Task<SourceRecordCreated> AddRecordAsync(
        SourceRecordInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw new ArgumentException("Title is required.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.RecordType))
        {
            throw new ArgumentException("RecordType is required.", nameof(input));
        }

        const string sql = """
            INSERT INTO research.source_record
                (source_record_id, tree_id, title, record_type, record_text, transcription,
                 record_date_text, record_date_from, record_date_to, record_year_from, record_year_to,
                 place_text, church_text, archive_name, fond, opis, sprava, page, citation_text,
                 source_url, status)
            VALUES
                (@source_record_id, @tree_id, @title, @record_type, @record_text, @transcription,
                 @record_date_text, @record_date_from, @record_date_to, @record_year_from, @record_year_to,
                 @place_text, @church_text, @archive_name, @fond, @opis, @sprava, @page, @citation_text,
                 @source_url, COALESCE(@status, 'inbox'))
            RETURNING source_record_id, status, created_at;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        var sourceRecordId = Guid.NewGuid();
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = input.TreeId });
        command.Parameters.Add(new NpgsqlParameter("title", NpgsqlDbType.Text) { Value = input.Title });
        command.Parameters.Add(new NpgsqlParameter("record_type", NpgsqlDbType.Text) { Value = input.RecordType });
        command.Parameters.Add(new NpgsqlParameter("record_text", NpgsqlDbType.Text) { Value = (object?)input.RecordText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("transcription", NpgsqlDbType.Text) { Value = (object?)input.Transcription ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_date_text", NpgsqlDbType.Text) { Value = (object?)input.RecordDateText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_date_from", NpgsqlDbType.Date) { Value = (object?)input.RecordDateFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_date_to", NpgsqlDbType.Date) { Value = (object?)input.RecordDateTo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_year_from", NpgsqlDbType.Smallint) { Value = (object?)input.RecordYearFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_year_to", NpgsqlDbType.Smallint) { Value = (object?)input.RecordYearTo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("place_text", NpgsqlDbType.Text) { Value = (object?)input.PlaceText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("church_text", NpgsqlDbType.Text) { Value = (object?)input.ChurchText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("archive_name", NpgsqlDbType.Text) { Value = (object?)input.ArchiveName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("fond", NpgsqlDbType.Text) { Value = (object?)input.Fond ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("opis", NpgsqlDbType.Text) { Value = (object?)input.Opis ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("sprava", NpgsqlDbType.Text) { Value = (object?)input.Sprava ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("page", NpgsqlDbType.Text) { Value = (object?)input.Page ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("citation_text", NpgsqlDbType.Text) { Value = (object?)input.CitationText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("source_url", NpgsqlDbType.Text) { Value = (object?)input.SourceUrl ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)input.Status ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Insert into research.source_record did not return a row.");
        }

        return new SourceRecordCreated(
            SourceRecordId: reader.GetFieldValue<Guid>(reader.GetOrdinal("source_record_id")),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")));
    }

    /// <summary>
    /// Reads one source record's core fields plus its keywords, person
    /// mentions, place mentions and link candidates. Returns null if the
    /// record does not exist. Binary attachments are never included — see
    /// <see cref="SourceRecordDetail.AttachmentsNote"/>.
    /// </summary>
    public async Task<SourceRecordDetail?> GetRecordAsync(
        Guid sourceRecordId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var core = await ReadCoreAsync(connection, sourceRecordId, cancellationToken).ConfigureAwait(false);
        if (core is null)
        {
            return null;
        }

        var keywords = await ReadKeywordsAsync(connection, sourceRecordId, cancellationToken).ConfigureAwait(false);
        var personMentions = await ReadPersonMentionsAsync(connection, sourceRecordId, cancellationToken).ConfigureAwait(false);
        var placeMentions = await ReadPlaceMentionsAsync(connection, sourceRecordId, cancellationToken).ConfigureAwait(false);
        var linkCandidates = await ReadLinkCandidatesAsync(connection, sourceRecordId, cancellationToken).ConfigureAwait(false);

        return core with
        {
            Keywords = keywords,
            PersonMentions = personMentions,
            PlaceMentions = placeMentions,
            LinkCandidates = linkCandidates,
        };
    }

    /// <summary>
    /// Updates the provided fields of a source record plus <c>updated_at</c>.
    /// A null argument leaves the corresponding column unchanged (this method
    /// has no way to explicitly clear a field back to NULL — pass a new
    /// non-null value or use a future dedicated "clear" operation for that).
    /// Returns null if the record does not exist.
    /// </summary>
    public async Task<SourceRecordUpdated?> UpdateRecordAsync(
        Guid sourceRecordId,
        string? title = null,
        string? recordType = null,
        string? recordText = null,
        string? transcription = null,
        string? recordDateText = null,
        DateOnly? recordDateFrom = null,
        DateOnly? recordDateTo = null,
        short? recordYearFrom = null,
        short? recordYearTo = null,
        string? placeText = null,
        string? churchText = null,
        string? archiveName = null,
        string? fond = null,
        string? opis = null,
        string? sprava = null,
        string? page = null,
        string? citationText = null,
        string? sourceUrl = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE research.source_record
            SET
                title             = COALESCE(@title, title),
                record_type       = COALESCE(@record_type, record_type),
                record_text       = COALESCE(@record_text, record_text),
                transcription     = COALESCE(@transcription, transcription),
                record_date_text  = COALESCE(@record_date_text, record_date_text),
                record_date_from  = COALESCE(@record_date_from, record_date_from),
                record_date_to    = COALESCE(@record_date_to, record_date_to),
                record_year_from  = COALESCE(@record_year_from, record_year_from),
                record_year_to    = COALESCE(@record_year_to, record_year_to),
                place_text        = COALESCE(@place_text, place_text),
                church_text       = COALESCE(@church_text, church_text),
                archive_name      = COALESCE(@archive_name, archive_name),
                fond              = COALESCE(@fond, fond),
                opis              = COALESCE(@opis, opis),
                sprava            = COALESCE(@sprava, sprava),
                page              = COALESCE(@page, page),
                citation_text     = COALESCE(@citation_text, citation_text),
                source_url        = COALESCE(@source_url, source_url),
                status            = COALESCE(@status, status),
                updated_at        = now()
            WHERE source_record_id = @source_record_id
            RETURNING source_record_id, status, updated_at;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("title", NpgsqlDbType.Text) { Value = (object?)title ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_type", NpgsqlDbType.Text) { Value = (object?)recordType ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_text", NpgsqlDbType.Text) { Value = (object?)recordText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("transcription", NpgsqlDbType.Text) { Value = (object?)transcription ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_date_text", NpgsqlDbType.Text) { Value = (object?)recordDateText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_date_from", NpgsqlDbType.Date) { Value = (object?)recordDateFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_date_to", NpgsqlDbType.Date) { Value = (object?)recordDateTo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_year_from", NpgsqlDbType.Smallint) { Value = (object?)recordYearFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("record_year_to", NpgsqlDbType.Smallint) { Value = (object?)recordYearTo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("place_text", NpgsqlDbType.Text) { Value = (object?)placeText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("church_text", NpgsqlDbType.Text) { Value = (object?)churchText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("archive_name", NpgsqlDbType.Text) { Value = (object?)archiveName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("fond", NpgsqlDbType.Text) { Value = (object?)fond ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("opis", NpgsqlDbType.Text) { Value = (object?)opis ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("sprava", NpgsqlDbType.Text) { Value = (object?)sprava ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("page", NpgsqlDbType.Text) { Value = (object?)page ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("citation_text", NpgsqlDbType.Text) { Value = (object?)citationText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("source_url", NpgsqlDbType.Text) { Value = (object?)sourceUrl ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SourceRecordUpdated(
            SourceRecordId: reader.GetFieldValue<Guid>(reader.GetOrdinal("source_record_id")),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
    }

    /// <summary>
    /// Attaches a search keyword to a record. Insert-or-ignore: the primary
    /// key is (source_record_id, keyword_type, keyword), so re-adding the same
    /// keyword is a harmless no-op.
    /// </summary>
    public async Task AddKeywordAsync(
        Guid sourceRecordId,
        string keyword,
        string keywordType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw new ArgumentException("Keyword is required.", nameof(keyword));
        }

        if (string.IsNullOrWhiteSpace(keywordType))
        {
            throw new ArgumentException("KeywordType is required.", nameof(keywordType));
        }

        const string sql = """
            INSERT INTO research.source_record_keyword (source_record_id, keyword, keyword_type)
            VALUES (@source_record_id, @keyword, @keyword_type)
            ON CONFLICT (source_record_id, keyword_type, keyword) DO NOTHING;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("keyword", NpgsqlDbType.Text) { Value = keyword });
        command.Parameters.Add(new NpgsqlParameter("keyword_type", NpgsqlDbType.Text) { Value = keywordType });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SourceRecordDetail?> ReadCoreAsync(
        NpgsqlConnection connection, Guid sourceRecordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_record_id, tree_id, title, record_type, record_text, transcription,
                   record_date_text, record_date_from, record_date_to, record_year_from, record_year_to,
                   place_text, church_text, archive_name, fond, opis, sprava, page, citation_text,
                   source_url, status, created_at, updated_at
            FROM research.source_record
            WHERE source_record_id = @source_record_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SourceRecordDetail(
            SourceRecordId: reader.GetFieldValue<Guid>(reader.GetOrdinal("source_record_id")),
            TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
            Title: reader.GetFieldValue<string>(reader.GetOrdinal("title")),
            RecordType: reader.GetFieldValue<string>(reader.GetOrdinal("record_type")),
            RecordText: reader.GetNullableString("record_text"),
            Transcription: reader.GetNullableString("transcription"),
            RecordDateText: reader.GetNullableString("record_date_text"),
            RecordDateFrom: reader.GetNullableValue<DateOnly>("record_date_from"),
            RecordDateTo: reader.GetNullableValue<DateOnly>("record_date_to"),
            RecordYearFrom: reader.GetNullableValue<short>("record_year_from"),
            RecordYearTo: reader.GetNullableValue<short>("record_year_to"),
            PlaceText: reader.GetNullableString("place_text"),
            ChurchText: reader.GetNullableString("church_text"),
            ArchiveName: reader.GetNullableString("archive_name"),
            Fond: reader.GetNullableString("fond"),
            Opis: reader.GetNullableString("opis"),
            Sprava: reader.GetNullableString("sprava"),
            Page: reader.GetNullableString("page"),
            CitationText: reader.GetNullableString("citation_text"),
            SourceUrl: reader.GetNullableString("source_url"),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            UpdatedAt: reader.GetNullableValue<DateTimeOffset>("updated_at"),
            Keywords: Array.Empty<SourceRecordKeywordEntry>(),
            PersonMentions: Array.Empty<PersonMentionEntry>(),
            PlaceMentions: Array.Empty<PlaceMentionEntry>(),
            LinkCandidates: Array.Empty<LinkCandidateEntry>());
    }

    private static async Task<IReadOnlyList<SourceRecordKeywordEntry>> ReadKeywordsAsync(
        NpgsqlConnection connection, Guid sourceRecordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT keyword, keyword_type
            FROM research.source_record_keyword
            WHERE source_record_id = @source_record_id
            ORDER BY keyword_type, keyword;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

        var results = new List<SourceRecordKeywordEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SourceRecordKeywordEntry(
                Keyword: reader.GetFieldValue<string>(reader.GetOrdinal("keyword")),
                KeywordType: reader.GetFieldValue<string>(reader.GetOrdinal("keyword_type"))));
        }

        return results;
    }

    private static async Task<IReadOnlyList<PersonMentionEntry>> ReadPersonMentionsAsync(
        NpgsqlConnection connection, Guid sourceRecordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT person_mention_id, name_text, given_name, surname, patronymic, sex, role, age_text,
                   estimated_birth_year, social_status, relationship_text, status, accepted_person_id,
                   confidence, created_at, updated_at
            FROM research.record_person_mention
            WHERE source_record_id = @source_record_id
            ORDER BY created_at, person_mention_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

        var results = new List<PersonMentionEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new PersonMentionEntry(
                PersonMentionId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_mention_id")),
                NameText: reader.GetNullableString("name_text"),
                GivenName: reader.GetNullableString("given_name"),
                Surname: reader.GetNullableString("surname"),
                Patronymic: reader.GetNullableString("patronymic"),
                Sex: reader.GetNullableChar("sex"),
                Role: reader.GetNullableString("role"),
                AgeText: reader.GetNullableString("age_text"),
                EstimatedBirthYear: reader.GetNullableValue<short>("estimated_birth_year"),
                SocialStatus: reader.GetNullableString("social_status"),
                RelationshipText: reader.GetNullableString("relationship_text"),
                Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
                AcceptedPersonId: reader.GetNullableValue<Guid>("accepted_person_id"),
                Confidence: reader.GetNullableValue<decimal>("confidence"),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                UpdatedAt: reader.GetNullableValue<DateTimeOffset>("updated_at")));
        }

        return results;
    }

    private static async Task<IReadOnlyList<PlaceMentionEntry>> ReadPlaceMentionsAsync(
        NpgsqlConnection connection, Guid sourceRecordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT place_mention_id, place_text, place_type, normalized_name, place_id
            FROM research.record_place_mention
            WHERE source_record_id = @source_record_id
            ORDER BY place_mention_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

        var results = new List<PlaceMentionEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new PlaceMentionEntry(
                PlaceMentionId: reader.GetFieldValue<Guid>(reader.GetOrdinal("place_mention_id")),
                PlaceText: reader.GetFieldValue<string>(reader.GetOrdinal("place_text")),
                PlaceType: reader.GetNullableString("place_type"),
                NormalizedName: reader.GetNullableString("normalized_name"),
                PlaceId: reader.GetNullableValue<long>("place_id")));
        }

        return results;
    }

    /// <summary>
    /// Link candidates hang off a mention, not directly off the record, so
    /// this joins through <c>record_person_mention</c> to scope them to the
    /// requested source record.
    /// </summary>
    private static async Task<IReadOnlyList<LinkCandidateEntry>> ReadLinkCandidatesAsync(
        NpgsqlConnection connection, Guid sourceRecordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT plc.person_link_candidate_id, plc.person_mention_id, plc.person_id, plc.score,
                   plc.explanation, plc.status, plc.created_at, plc.decided_at
            FROM research.person_link_candidate plc
            INNER JOIN research.record_person_mention rpm
                ON rpm.person_mention_id = plc.person_mention_id
            WHERE rpm.source_record_id = @source_record_id
            ORDER BY plc.person_mention_id, plc.score DESC, plc.created_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

        var results = new List<LinkCandidateEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new LinkCandidateEntry(
                PersonLinkCandidateId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_link_candidate_id")),
                PersonMentionId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_mention_id")),
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                Score: reader.GetFieldValue<decimal>(reader.GetOrdinal("score")),
                Explanation: reader.GetNullableString("explanation"),
                Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                DecidedAt: reader.GetNullableValue<DateTimeOffset>("decided_at")));
        }

        return results;
    }
}
