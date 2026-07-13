using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Research;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 6 Task 2 exit criteria for the binary attachment pipeline
/// (<see cref="AttachmentRepository"/>). Mirrors <see cref="DatabaseLifecycleTests"/>:
/// a fresh, uniquely named database per test class, migrations 0001-0009 applied
/// by <see cref="MigrationEngine"/>, then a tree + two source records seeded.
///
/// Test bytes are SYNTHESIZED in-memory (minimal valid signatures) so the tests
/// do not depend on any repo files: a PNG (8-byte signature), a JPEG (FF D8 FF)
/// and a disallowed random/text blob.
/// </summary>
public sealed class AttachmentPipelineTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private NpgsqlConnectionFactory _connectionFactory = null!;

    private Guid _treeId;
    private Guid _recordA;
    private Guid _recordB;

    public AttachmentPipelineTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);

        var options = _fixture.BuildOptionsForDatabase(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(options);
        _connectionFactory = new NpgsqlConnectionFactory(options);

        new MigrationEngine(_connectionString).Migrate();

        _treeId = Guid.NewGuid();
        _recordA = Guid.NewGuid();
        _recordB = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeId, "Attachment Tree");
        await InsertSourceRecordAsync(connection, _recordA, _treeId, "Record A", "birth");
        await InsertSourceRecordAsync(connection, _recordB, _treeId, "Record B", "death");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task AddThenGet_ReturnsByteIdenticalContent_AndSniffedMime()
    {
        var repo = new AttachmentRepository(_connectionFactory, new AttachmentOptions());
        var png = MakePng();

        var added = await repo.AddAttachmentAsync(
            _recordA, png, "screenshot.png", "A screenshot", "image", sequenceNo: 0, sourceUrl: null,
            cancellationToken: default);

        Assert.Equal("image/png", added.MimeType);
        Assert.False(added.Deduplicated);
        Assert.Equal(png.LongLength, added.ByteLength);

        var fetched = await repo.GetAttachmentAsync(added.SourceRecordAttachmentId);
        Assert.NotNull(fetched);
        Assert.True(png.SequenceEqual(fetched!.Content), "stored bytes must be byte-identical");
        Assert.Equal("image/png", fetched.MimeType);
        Assert.Equal("screenshot.png", fetched.FileName);
        Assert.Equal(added.ContentHash, fetched.ContentHash);
    }

    [Fact]
    public async Task AddSameBytesToTwoRecords_DedupsContent_KeepsTwoLinks()
    {
        var repo = new AttachmentRepository(_connectionFactory, new AttachmentOptions());
        var jpeg = MakeJpeg();

        var first = await repo.AddAttachmentAsync(
            _recordA, jpeg, "scan.jpg", null, "image", 0, null, default);
        Assert.False(first.Deduplicated);

        var second = await repo.AddAttachmentAsync(
            _recordB, jpeg, "scan-copy.jpg", null, "image", 0, null, default);

        Assert.True(second.Deduplicated);
        Assert.Equal(first.AttachmentContentId, second.AttachmentContentId);
        Assert.NotEqual(first.SourceRecordAttachmentId, second.SourceRecordAttachmentId);

        Assert.Equal(1, await CountContentRowsForHashAsync(first.ContentHash));
        Assert.Equal(2, await CountLinksForContentAsync(first.AttachmentContentId));
    }

    [Fact]
    public async Task AddOversizeContent_Throws_AttachmentTooLarge()
    {
        var repo = new AttachmentRepository(_connectionFactory, new AttachmentOptions { MaxFileBytes = 16 });
        var png = MakePng(payloadBytes: 64); // well over the 16-byte limit

        await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
            repo.AddAttachmentAsync(_recordA, png, "big.png", null, "image", 0, null, default));
    }

    [Fact]
    public async Task AddDisallowedContent_Throws_TypeNotAllowed()
    {
        var repo = new AttachmentRepository(_connectionFactory, new AttachmentOptions());
        var blob = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x74, 0x65, 0x78, 0x74, 0x21 }; // random/text bytes

        await Assert.ThrowsAsync<AttachmentTypeNotAllowedException>(() =>
            repo.AddAttachmentAsync(_recordA, blob, "notes.txt", null, "document", 0, null, default));
    }

    [Fact]
    public async Task AddBeyondRecordQuota_Throws_QuotaExceeded()
    {
        // Room for one ~60-byte attachment but not two distinct ones.
        var repo = new AttachmentRepository(
            _connectionFactory,
            new AttachmentOptions { MaxFileBytes = 1024, MaxRecordBytes = 100 });

        var png = MakePng(payloadBytes: 50);   // ~58 bytes
        var jpeg = MakeJpeg(payloadBytes: 50);  // ~53 bytes, distinct content

        await repo.AddAttachmentAsync(_recordA, png, "a.png", null, "image", 0, null, default);

        await Assert.ThrowsAsync<RecordAttachmentQuotaExceededException>(() =>
            repo.AddAttachmentAsync(_recordA, jpeg, "b.jpg", null, "image", 1, null, default));
    }

    [Fact]
    public async Task DeleteLink_RemovesLink_ButContentSurvives_ThenCleanupReclaims()
    {
        var repo = new AttachmentRepository(_connectionFactory, new AttachmentOptions());
        var png = MakePng();

        var added = await repo.AddAttachmentAsync(
            _recordA, png, "doc.png", null, "image", 0, null, default);

        var deleted = await repo.DeleteAttachmentLinkAsync(added.SourceRecordAttachmentId);
        Assert.True(deleted);

        // Link is gone but the shared content row survives (no cascade, no
        // side-effect cleanup).
        Assert.Null(await repo.GetAttachmentAsync(added.SourceRecordAttachmentId));
        Assert.Equal(1, await CountContentRowsForHashAsync(added.ContentHash));

        // Explicit cleanup reclaims exactly the now-orphaned content row.
        var removed = await repo.CleanupOrphanedContentAsync();
        Assert.Equal(1, removed);
        Assert.Equal(0, await CountContentRowsForHashAsync(added.ContentHash));
    }

    // -- synthesized test bytes (no repo-file dependency) --

    /// <summary>Minimal PNG: the 8-byte signature followed by filler payload.</summary>
    private static byte[] MakePng(int payloadBytes = 8)
    {
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        return WithFiller(signature, payloadBytes, filler: 0x11);
    }

    /// <summary>Minimal JPEG: the FF D8 FF SOI marker followed by filler payload.</summary>
    private static byte[] MakeJpeg(int payloadBytes = 8)
    {
        var signature = new byte[] { 0xFF, 0xD8, 0xFF };
        return WithFiller(signature, payloadBytes, filler: 0x22);
    }

    private static byte[] WithFiller(byte[] signature, int payloadBytes, byte filler)
    {
        var result = new byte[signature.Length + payloadBytes];
        Array.Copy(signature, result, signature.Length);
        for (var i = signature.Length; i < result.Length; i++)
        {
            result[i] = filler;
        }

        return result;
    }

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

    private async Task<int> CountContentRowsForHashAsync(string contentHash)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.attachment_content WHERE content_hash = @h;", connection);
        command.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Char) { Value = contentHash });
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> CountLinksForContentAsync(long attachmentContentId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.source_record_attachment WHERE attachment_content_id = @id;", connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = attachmentContentId });
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}
