using System.Security.Cryptography;
using System.Text;
using Genealogy.Workspace.Data;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 6 exit criteria for the Evidence Inbox <c>research</c> schema
/// (migration 0009_research_evidence_schema.sql). Exercises the three
/// database-level invariants directly via raw parameterized Npgsql commands
/// (so the exact <see cref="PostgresException"/> surfaces with no repository
/// translation in the way):
///   * DEDUP: attachment_content.content_hash is UNIQUE — identical bytes
///     cannot be stored twice.
///   * SAME-TREE: an accepted mention link and a person_link_candidate can
///     only target a genealogy.person in the SAME tree as the evidence record;
///     a cross-tree link is rejected by the composite FK.
///   * CASCADE ASYMMETRY: deleting a source_record removes its attachment LINK
///     row, but the shared/deduped attachment_content row SURVIVES.
/// The fixture mirrors <see cref="DatabaseLifecycleTests"/>: a fresh, uniquely
/// named database per test class, migrations 0001-0009 applied by
/// <see cref="MigrationEngine"/>, then a tree + person seeded per tree.
/// </summary>
public sealed class ResearchSchemaTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    private Guid _treeA;
    private Guid _treeB;
    private Guid _personA1;
    private Guid _personB1;

    public ResearchSchemaTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(
            _fixture.BuildOptionsForDatabase(_databaseName));

        new MigrationEngine(_connectionString).Migrate();

        _treeA = Guid.NewGuid();
        _treeB = Guid.NewGuid();
        _personA1 = Guid.NewGuid();
        _personB1 = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeA, "Research Tree A");
        await TestSeeding.InsertTreeAsync(connection, _treeB, "Research Tree B");
        await TestSeeding.InsertPersonAsync(connection, _treeA, _personA1, primaryDisplayName: "Person A1");
        await TestSeeding.InsertPersonAsync(connection, _treeB, _personB1, primaryDisplayName: "Person B1");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task SourceRecord_WithAttachmentContentAndLink_Inserts()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var recordId = Guid.NewGuid();
        await InsertSourceRecordAsync(connection, recordId, _treeA, "Birth of A1", "birth");

        var bytes = Encoding.UTF8.GetBytes("scanned-metric-book-page-1");
        var contentId = await InsertAttachmentContentAsync(connection, bytes);
        var linkId = await InsertAttachmentLinkAsync(connection, recordId, contentId, sequenceNo: 0);

        Assert.True(contentId > 0);
        Assert.True(linkId > 0);
    }

    [Fact]
    public async Task AttachmentContent_DuplicateContentHash_IsRejected()
    {
        // DEDUP: attachment_content.content_hash is UNIQUE — the same bytes
        // (hence the same SHA-256) cannot be inserted a second time.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var bytes = Encoding.UTF8.GetBytes("identical-bytes-stored-once");
        await InsertAttachmentContentAsync(connection, bytes);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAttachmentContentAsync(connection, bytes));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task RecordPersonMention_AcceptedPersonInDifferentTree_IsRejected()
    {
        // SAME-TREE: fk_mention_accepted_person is the composite FK
        // (tree_id, accepted_person_id) -> genealogy.person (tree_id, person_id).
        // The record (and thus the mention) is in tree A, but _personB1 belongs
        // to tree B, so no (treeA, personB1) person row exists to satisfy the FK.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var recordId = Guid.NewGuid();
        await InsertSourceRecordAsync(connection, recordId, _treeA, "Marriage record", "marriage");

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertPersonMentionAsync(
                connection, Guid.NewGuid(), _treeA, recordId,
                status: "accepted", acceptedPersonId: _personB1));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task PersonLinkCandidate_TargetInDifferentTree_IsRejected()
    {
        // SAME-TREE: fk_candidate_person is the composite FK
        // (tree_id, person_id) -> genealogy.person (tree_id, person_id).
        // The candidate claims tree A but targets _personB1 (tree B).
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var recordId = Guid.NewGuid();
        await InsertSourceRecordAsync(connection, recordId, _treeA, "Death record", "death");

        var mentionId = Guid.NewGuid();
        await InsertPersonMentionAsync(connection, mentionId, _treeA, recordId, status: "unlinked");

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertLinkCandidateAsync(
                connection, Guid.NewGuid(), mentionId, _treeA, _personB1, score: 0.75m));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task DeletingSourceRecord_RemovesAttachmentLink_ButContentSurvives()
    {
        // CASCADE ASYMMETRY: source_record_attachment.source_record_id cascades,
        // but attachment_content is never cascaded and must survive.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var recordId = Guid.NewGuid();
        await InsertSourceRecordAsync(connection, recordId, _treeA, "Confession list", "confession");

        var bytes = Encoding.UTF8.GetBytes("confession-list-scan");
        var contentId = await InsertAttachmentContentAsync(connection, bytes);
        await InsertAttachmentLinkAsync(connection, recordId, contentId, sequenceNo: 0);

        await DeleteSourceRecordAsync(connection, recordId);

        Assert.Equal(0, await CountAttachmentLinksAsync(connection, contentId));
        Assert.Equal(1, await CountAttachmentContentAsync(connection, contentId));
    }

    [Fact]
    public async Task RecordPersonMention_AcceptedPersonInSameTree_Inserts()
    {
        // SAME-TREE happy path: an accepted link to a person in the record's own
        // tree satisfies the composite FK.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var recordId = Guid.NewGuid();
        await InsertSourceRecordAsync(connection, recordId, _treeA, "Revision list", "revision");

        var mentionId = Guid.NewGuid();
        await InsertPersonMentionAsync(
            connection, mentionId, _treeA, recordId,
            status: "accepted", acceptedPersonId: _personA1, confidence: 0.92m);

        // A same-tree candidate for the same person also inserts fine.
        await InsertLinkCandidateAsync(
            connection, Guid.NewGuid(), mentionId, _treeA, _personA1, score: 0.92m, status: "accepted");
    }

    // -- raw insert helpers (local to this test; no repository write path exists yet) --

    private static async Task InsertSourceRecordAsync(
        NpgsqlConnection connection, Guid sourceRecordId, Guid treeId, string title, string recordType)
    {
        const string sql = """
            INSERT INTO research.source_record (source_record_id, tree_id, title, record_type)
            VALUES (@source_record_id, @tree_id, @title, @record_type);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("title", NpgsqlDbType.Text) { Value = title });
        command.Parameters.Add(new NpgsqlParameter("record_type", NpgsqlDbType.Text) { Value = recordType });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertAttachmentContentAsync(NpgsqlConnection connection, byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        const string sql = """
            INSERT INTO research.attachment_content (content_hash, content, byte_length, mime_type)
            VALUES (@content_hash, @content, @byte_length, @mime_type)
            RETURNING attachment_content_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("content_hash", NpgsqlDbType.Char) { Value = hash });
        command.Parameters.Add(new NpgsqlParameter("content", NpgsqlDbType.Bytea) { Value = content });
        command.Parameters.Add(new NpgsqlParameter("byte_length", NpgsqlDbType.Bigint) { Value = (long)content.Length });
        command.Parameters.Add(new NpgsqlParameter("mime_type", NpgsqlDbType.Text) { Value = "image/jpeg" });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> InsertAttachmentLinkAsync(
        NpgsqlConnection connection, Guid sourceRecordId, long attachmentContentId, int sequenceNo)
    {
        const string sql = """
            INSERT INTO research.source_record_attachment
                (source_record_id, attachment_content_id, sequence_no, attachment_type)
            VALUES (@source_record_id, @attachment_content_id, @sequence_no, 'image')
            RETURNING source_record_attachment_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("attachment_content_id", NpgsqlDbType.Bigint) { Value = attachmentContentId });
        command.Parameters.Add(new NpgsqlParameter("sequence_no", NpgsqlDbType.Integer) { Value = sequenceNo });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertPersonMentionAsync(
        NpgsqlConnection connection, Guid personMentionId, Guid treeId, Guid sourceRecordId,
        string status = "unlinked", Guid? acceptedPersonId = null, decimal? confidence = null)
    {
        const string sql = """
            INSERT INTO research.record_person_mention
                (person_mention_id, tree_id, source_record_id, status, accepted_person_id, confidence)
            VALUES
                (@person_mention_id, @tree_id, @source_record_id, @status, @accepted_person_id, @confidence);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = status });
        command.Parameters.Add(new NpgsqlParameter("accepted_person_id", NpgsqlDbType.Uuid) { Value = (object?)acceptedPersonId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("confidence", NpgsqlDbType.Numeric) { Value = (object?)confidence ?? DBNull.Value });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLinkCandidateAsync(
        NpgsqlConnection connection, Guid candidateId, Guid personMentionId, Guid treeId, Guid personId,
        decimal score, string status = "suggested")
    {
        const string sql = """
            INSERT INTO research.person_link_candidate
                (person_link_candidate_id, person_mention_id, tree_id, person_id, score, status)
            VALUES
                (@person_link_candidate_id, @person_mention_id, @tree_id, @person_id, @score, @status);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_link_candidate_id", NpgsqlDbType.Uuid) { Value = candidateId });
        command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("score", NpgsqlDbType.Numeric) { Value = score });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = status });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteSourceRecordAsync(NpgsqlConnection connection, Guid sourceRecordId)
    {
        await using var command = new NpgsqlCommand(
            "DELETE FROM research.source_record WHERE source_record_id = @id;", connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAttachmentLinksAsync(NpgsqlConnection connection, long attachmentContentId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.source_record_attachment WHERE attachment_content_id = @id;", connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = attachmentContentId });
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountAttachmentContentAsync(NpgsqlConnection connection, long attachmentContentId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.attachment_content WHERE attachment_content_id = @id;", connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = attachmentContentId });
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}
