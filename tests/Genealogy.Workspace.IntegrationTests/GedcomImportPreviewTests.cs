using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 3 exit criterion for the GEDCOM import PREVIEW port: exercises
/// <see cref="GedcomImportPreviewService.PreviewAsync"/> against a small
/// hand-built scenario seeded directly (no dependency on the artifact loader,
/// which a parallel task owns).
///
/// Scenario (one tree):
///   Production persons P1 (identical in staging), P2 (changed in staging),
///   P3 (production-only). Staging persons P1, P2 (changed), P4 (new).
///   => Person ADD = 1 (P4), Person UPDATE = 1 (P2), MISSING_FROM_IMPORT = 1 (P3).
///   One staged place absent from the global place table => Place ADD = 1.
///   One production event with external key EVT1 on P1, plus a staged event
///   with the SAME key and a staged citation on it => EventCitation REPLACE = 1
///   (and no Event-level change, since the staged event matches by key).
/// Also asserts the STAGED -> PREVIEWED status flip and the person-change sample.
/// </summary>
public sealed class GedcomImportPreviewTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private GedcomImportPreviewService _service = null!;

    private readonly Guid _tree = Guid.NewGuid();
    private readonly Guid _batch = Guid.NewGuid();
    private readonly Guid _p1 = Guid.NewGuid();  // identical in prod + staging
    private readonly Guid _p2 = Guid.NewGuid();  // changed in staging (UPDATE)
    private readonly Guid _p3 = Guid.NewGuid();  // production-only (MISSING_FROM_IMPORT)
    private readonly Guid _p4 = Guid.NewGuid();  // staging-only (ADD)

    public GedcomImportPreviewTests(WorkspaceEnvironmentFixture fixture)
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

        _service = new GedcomImportPreviewService(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // --- Production tree + persons ---
        await TestSeeding.InsertTreeAsync(connection, _tree, "Preview Test Tree");
        await TestSeeding.InsertPersonAsync(connection, _tree, _p1,
            externalId: "I1", primaryDisplayName: "John Doe", surnameNormalized: "doe", sex: 'M', isLiving: false);
        await TestSeeding.InsertPersonAsync(connection, _tree, _p2,
            externalId: "I2", primaryDisplayName: "Jane Old", surnameNormalized: "old", sex: 'F', isLiving: true);
        await TestSeeding.InsertPersonAsync(connection, _tree, _p3,
            externalId: "I3", primaryDisplayName: "Ghost Person", surnameNormalized: "ghost", sex: 'M', isLiving: false);

        // Production event on P1 carrying an external key; the staged event
        // reuses this key so the staged citation REPLACEs rather than ADDs.
        await InsertProdEventAsync(connection, _tree, _p1, "EVT1", "BIRT");

        // --- Staging batch + rows ---
        await InsertBatchAsync(connection, _batch, _tree, "STAGED");

        // P1 staged identical to production (must not produce any change).
        await InsertStagingPersonAsync(connection, _batch, _p1, "I1", 'M', false, "John Doe", "doe");
        // P2 staged with a changed display name + surname (UPDATE).
        await InsertStagingPersonAsync(connection, _batch, _p2, "I2", 'F', true, "Jane New", "new");
        // P4 staged only (ADD).
        await InsertStagingPersonAsync(connection, _batch, _p4, "I4", 'M', null, "New Kid", "kid");

        // Staged place absent from the global place table (Place ADD).
        await InsertStagingPlaceAsync(connection, _batch, 1, "Kyiv, Ukraine");

        // Staged event reusing EVT1 on P1, and a citation on it (EventCitation REPLACE).
        await InsertStagingEventAsync(connection, _batch, 1, "EVT1", _p1, "BIRT");
        await InsertStagingCitationAsync(connection, _batch, 1, 1, "SRC-1");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task PreviewAsync_UnknownBatch_Throws()
    {
        await Assert.ThrowsAsync<GedcomImportBatchNotFoundException>(
            () => _service.PreviewAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task PreviewAsync_ClassifiesChanges_AndFlipsStatus()
    {
        var result = await _service.PreviewAsync(_batch);

        // Batch header round-trips and reflects the STAGED -> PREVIEWED flip.
        Assert.Equal(_batch, result.Batch.ImportBatchId);
        Assert.Equal(_tree, result.Batch.TreeId);
        Assert.Equal("Preview Test Tree", result.Batch.TreeName);
        Assert.Equal("PREVIEWED", result.Batch.Status);
        Assert.NotNull(result.Batch.PreviewedAt);

        long CountOf(string entity, string change) =>
            result.Changes.SingleOrDefault(c => c.EntityType == entity && c.ChangeType == change)?.RowCount ?? 0;

        Assert.Equal(1, CountOf("Person", "ADD"));
        Assert.Equal(1, CountOf("Person", "UPDATE"));
        Assert.Equal(1, CountOf("Person", "MISSING_FROM_IMPORT"));
        Assert.Equal(1, CountOf("Place", "ADD"));
        Assert.Equal(1, CountOf("EventCitation", "REPLACE"));

        // No Event-level change: the staged event matches production by key.
        Assert.Equal(0, CountOf("Event", "ADD"));
        Assert.Equal(0, CountOf("Event", "UPDATE"));
        Assert.Equal(0, CountOf("Event", "MISSING_FROM_IMPORT"));
        Assert.Equal(0, CountOf("EventCitation", "ADD"));

        // Every reported change has a positive count and the set is ordered.
        Assert.All(result.Changes, c => Assert.True(c.RowCount > 0));
        var ordered = result.Changes
            .OrderBy(c => c.EntityType, StringComparer.Ordinal)
            .ThenBy(c => c.ChangeType, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(ordered, result.Changes);

        // Person sample: P4 (ADD) and P2 (UPDATE); P1 (unchanged) excluded.
        Assert.Equal(2, result.SamplePersonChanges.Count);
        var add = Assert.Single(result.SamplePersonChanges, s => s.ChangeType == "ADD");
        Assert.Equal(_p4, add.TreePersonId);
        Assert.Equal("New Kid", add.PrimaryDisplayName);
        Assert.Null(add.CurrentPrimaryDisplayName);

        var update = Assert.Single(result.SamplePersonChanges, s => s.ChangeType == "UPDATE");
        Assert.Equal(_p2, update.TreePersonId);
        Assert.Equal("Jane New", update.PrimaryDisplayName);
        Assert.Equal("Jane Old", update.CurrentPrimaryDisplayName);
    }

    [Fact]
    public async Task PreviewAsync_NonStagedBatch_DoesNotChangeStatus()
    {
        // Advance the batch past STAGED, then confirm preview leaves it alone.
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection,
                "UPDATE genealogy.gedcom_import_batch SET status = 'APPLIED' WHERE import_batch_id = @b;",
                ("b", NpgsqlDbType.Uuid, _batch));
        }

        var result = await _service.PreviewAsync(_batch);

        Assert.Equal("APPLIED", result.Batch.Status);
    }

    // --- Raw staging/production insert helpers (kept local to avoid touching
    //     the shared TestSeeding helper another task may edit in parallel) ---

    private async Task InsertBatchAsync(NpgsqlConnection connection, Guid batch, Guid tree, string status)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_batch
                (import_batch_id, source_file_path, tree_id, status, person_count, place_count, event_count)
            VALUES (@b, @path, @tree, @status, 3, 1, 1);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("path", NpgsqlDbType.Text, "/tmp/preview-test.ged"),
            ("tree", NpgsqlDbType.Uuid, tree),
            ("status", NpgsqlDbType.Varchar, status));
    }

    private async Task InsertStagingPersonAsync(
        NpgsqlConnection connection, Guid batch, Guid personId,
        string? externalId, char? sex, bool? isLiving, string? displayName, string? surnameNormalized)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person
                (import_batch_id, tree_person_id, external_id, sex, is_living, primary_display_name, surname_normalized)
            VALUES (@b, @id, @ext, @sex, @living, @name, @surname);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("id", NpgsqlDbType.Uuid, personId),
            ("ext", NpgsqlDbType.Varchar, (object?)externalId ?? DBNull.Value),
            ("sex", NpgsqlDbType.Char, sex is null ? DBNull.Value : sex.Value.ToString()),
            ("living", NpgsqlDbType.Boolean, (object?)isLiving ?? DBNull.Value),
            ("name", NpgsqlDbType.Text, (object?)displayName ?? DBNull.Value),
            ("surname", NpgsqlDbType.Text, (object?)surnameNormalized ?? DBNull.Value));
    }

    private async Task InsertStagingPlaceAsync(NpgsqlConnection connection, Guid batch, int rowNumber, string placeRaw)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_place (import_batch_id, row_number, place_raw)
            VALUES (@b, @row, @place);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, rowNumber),
            ("place", NpgsqlDbType.Text, placeRaw));
    }

    private async Task InsertStagingEventAsync(
        NpgsqlConnection connection, Guid batch, int rowNumber, string externalKey, Guid personId, string eventType)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event
                (import_batch_id, row_number, external_event_key, tree_person_id, event_type, is_derived)
            VALUES (@b, @row, @key, @person, @type, false);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, rowNumber),
            ("key", NpgsqlDbType.Varchar, externalKey),
            ("person", NpgsqlDbType.Uuid, personId),
            ("type", NpgsqlDbType.Varchar, eventType));
    }

    private async Task InsertStagingCitationAsync(
        NpgsqlConnection connection, Guid batch, int rowNumber, int eventRowNumber, string sourceRef)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event_citation
                (import_batch_id, row_number, event_row_number, source_ref)
            VALUES (@b, @row, @evrow, @ref);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, rowNumber),
            ("evrow", NpgsqlDbType.Integer, eventRowNumber),
            ("ref", NpgsqlDbType.Text, sourceRef));
    }

    private async Task InsertProdEventAsync(
        NpgsqlConnection connection, Guid tree, Guid personId, string externalKey, string eventType)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.event
                (tree_id, person_id, event_type, external_event_key, is_derived)
            VALUES (@tree, @person, @type, @key, false);
            """,
            ("tree", NpgsqlDbType.Uuid, tree),
            ("person", NpgsqlDbType.Uuid, personId),
            ("type", NpgsqlDbType.Varchar, eventType),
            ("key", NpgsqlDbType.Varchar, externalKey));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, params (string Name, NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, type, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        }

        await command.ExecuteNonQueryAsync();
    }
}
