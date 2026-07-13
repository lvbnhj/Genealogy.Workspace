using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 4, Task E exit criterion for <see cref="GedcomApplyService"/>: the
/// thin .NET wrapper around <c>genealogy.apply_gedcom_import</c> (migration
/// 0008), with dry-run routed to <see cref="GedcomImportPreviewService"/>
/// instead of touching production data.
///
/// Scenario (one tree, one batch, status WAITING_FOR_CONFIRMATION, nothing yet
/// in production):
///   - persons Root (the batch's declared root) and Spouse, joined by a staged
///     family;
///   - person Child, linked to the family (family_child) and to Root via a
///     staged parent_of edge;
///   - a staged place, a staged name, a staged BIRT event on Root with a
///     citation.
/// A real apply should therefore report Person ADD = 3, PersonName ADD = 1,
/// Place ADD = 1, Family ADD = 1, FamilyChild ADD = 1, ParentOf ADD = 1,
/// Event ADD = 1, EventCitation REPLACE = 1, and leave production with 3
/// persons in the tree.
/// </summary>
public sealed class GedcomApplyServiceTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private GedcomApplyService _service = null!;

    private readonly Guid _tree = Guid.NewGuid();
    private readonly Guid _batch = Guid.NewGuid();
    private readonly Guid _root = Guid.NewGuid();
    private readonly Guid _spouse = Guid.NewGuid();
    private readonly Guid _child = Guid.NewGuid();
    private readonly Guid _family = Guid.NewGuid();

    public GedcomApplyServiceTests(WorkspaceEnvironmentFixture fixture)
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

        _service = new GedcomApplyService(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _tree, "Apply Service Test Tree");
        await InsertBatchAsync(connection, _batch, _tree, _root, "WAITING_FOR_CONFIRMATION");

        await InsertStagingPersonAsync(connection, _batch, _root, "I1", 'M', false, "Root Person", "root");
        await InsertStagingPersonAsync(connection, _batch, _spouse, "I2", 'F', false, "Spouse Person", "spouse");
        await InsertStagingPersonAsync(connection, _batch, _child, "I3", 'F', false, "Child Person", "child");

        await InsertStagingPersonNameAsync(connection, _batch, rowNumber: 1, _root, "Root Person", "root person");

        await InsertStagingPlaceAsync(connection, _batch, rowNumber: 1, "Kyiv, Ukraine");

        await InsertStagingFamilyAsync(connection, _batch, _family, _root, _spouse, marriageYear: 1920);
        await InsertStagingFamilyChildAsync(connection, _batch, _family, _child);
        await InsertStagingParentOfAsync(connection, _batch, parent: _root, child: _child);

        await InsertStagingEventAsync(connection, _batch, rowNumber: 1, externalKey: "EVT-ROOT-BIRT", _root, "BIRT");
        await InsertStagingCitationAsync(connection, _batch, rowNumber: 1, eventRowNumber: 1, sourceRef: "SRC-1");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task ApplyAsync_DryRunDefault_DoesNotApply_AndDelegatesToPreview()
    {
        var result = await _service.ApplyAsync(_batch);

        Assert.True(result.DryRun);
        Assert.Contains("Dry run", result.Note, StringComparison.OrdinalIgnoreCase);

        // Preview only flips STAGED -> PREVIEWED; a WAITING_FOR_CONFIRMATION
        // batch is left exactly as-is (0005 preview semantics), and it must
        // not have advanced to APPLIED.
        Assert.Equal("WAITING_FOR_CONFIRMATION", result.Status);
        Assert.NotEqual("APPLIED", result.Status);

        long CountOf(string entity, string change) =>
            result.Changes.SingleOrDefault(c => c.EntityType == entity && c.ChangeType == change)?.RowCount ?? 0;

        Assert.Equal(3, CountOf("Person", "ADD"));
        Assert.Equal(1, CountOf("Family", "ADD"));

        // No production side effects from a dry run.
        Assert.Equal(0, await CountProductionPersonsAsync());

        var dbStatus = await ReadBatchStatusAsync(_batch);
        Assert.Equal("WAITING_FOR_CONFIRMATION", dbStatus);
    }

    [Fact]
    public async Task ApplyAsync_RealApply_AppliesChanges_AndFlipsStatusToApplied()
    {
        var result = await _service.ApplyAsync(_batch, dryRun: false);

        Assert.False(result.DryRun);
        Assert.Equal("APPLIED", result.Status);
        Assert.DoesNotContain("Dry run", result.Note, StringComparison.OrdinalIgnoreCase);

        long CountOf(string entity, string change) =>
            result.Changes.SingleOrDefault(c => c.EntityType == entity && c.ChangeType == change)?.RowCount ?? 0;

        Assert.Equal(3, CountOf("Person", "ADD"));
        Assert.Equal(1, CountOf("PersonName", "ADD"));
        Assert.Equal(1, CountOf("Place", "ADD"));
        Assert.Equal(1, CountOf("Family", "ADD"));
        Assert.Equal(1, CountOf("FamilyChild", "ADD"));
        Assert.Equal(1, CountOf("ParentOf", "ADD"));
        Assert.Equal(1, CountOf("Event", "ADD"));
        Assert.Equal(1, CountOf("EventCitation", "REPLACE"));

        // Production now actually has the persons.
        Assert.Equal(3, await CountProductionPersonsAsync());

        var dbStatus = await ReadBatchStatusAsync(_batch);
        Assert.Equal("APPLIED", dbStatus);
    }

    [Fact]
    public async Task ApplyAsync_OnAlreadyAppliedBatch_ThrowsInvalidOperationException()
    {
        await _service.ApplyAsync(_batch, dryRun: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ApplyAsync(_batch, dryRun: false));

        Assert.Contains("not in an applyable status", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_UnknownBatch_ThrowsGedcomImportBatchNotFoundException()
    {
        await Assert.ThrowsAsync<GedcomImportBatchNotFoundException>(
            () => _service.ApplyAsync(Guid.NewGuid()));
    }

    private async Task<int> CountProductionPersonsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM genealogy.person WHERE tree_id = @tree;", connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = _tree });

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<string> ReadBatchStatusAsync(Guid batch)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT status FROM genealogy.gedcom_import_batch WHERE import_batch_id = @b;", connection);
        command.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = batch });

        return (string)(await command.ExecuteScalarAsync())!;
    }

    // --- seed helpers (kept local to avoid touching the shared TestSeeding
    //     helper another task may edit in parallel) ---

    private async Task InsertBatchAsync(
        NpgsqlConnection connection, Guid batch, Guid tree, Guid rootPersonId, string status)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_batch
                (import_batch_id, source_file_path, tree_id, root_person_id, status)
            VALUES (@b, @path, @tree, @root, @status);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("path", NpgsqlDbType.Text, "/tmp/apply-service-test.ged"),
            ("tree", NpgsqlDbType.Uuid, tree),
            ("root", NpgsqlDbType.Uuid, rootPersonId),
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

    private async Task InsertStagingPersonNameAsync(
        NpgsqlConnection connection, Guid batch, int rowNumber, Guid personId, string fullName, string fullNameNormalized)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person_name
                (import_batch_id, row_number, tree_person_id, script_code, name_type,
                 full_name, full_name_normalized, is_primary)
            VALUES (@b, @row, @id, 'LATN', 'BIRTH', @full, @norm, true);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, rowNumber),
            ("id", NpgsqlDbType.Uuid, personId),
            ("full", NpgsqlDbType.Text, fullName),
            ("norm", NpgsqlDbType.Text, fullNameNormalized));
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

    private async Task InsertStagingFamilyAsync(
        NpgsqlConnection connection, Guid batch, Guid familyId, Guid spouse1, Guid spouse2, short marriageYear)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_family
                (import_batch_id, family_id, spouse1_tree_person_id, spouse2_tree_person_id, marriage_year)
            VALUES (@b, @family, @spouse1, @spouse2, @year);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("family", NpgsqlDbType.Uuid, familyId),
            ("spouse1", NpgsqlDbType.Uuid, spouse1),
            ("spouse2", NpgsqlDbType.Uuid, spouse2),
            ("year", NpgsqlDbType.Smallint, marriageYear));
    }

    private async Task InsertStagingFamilyChildAsync(NpgsqlConnection connection, Guid batch, Guid familyId, Guid childId)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_family_child (import_batch_id, family_id, child_tree_person_id)
            VALUES (@b, @family, @child);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("family", NpgsqlDbType.Uuid, familyId),
            ("child", NpgsqlDbType.Uuid, childId));
    }

    private async Task InsertStagingParentOfAsync(NpgsqlConnection connection, Guid batch, Guid parent, Guid child)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_parent_of
                (import_batch_id, parent_tree_person_id, child_tree_person_id, relation_type)
            VALUES (@b, @parent, @child, 'birth');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("parent", NpgsqlDbType.Uuid, parent),
            ("child", NpgsqlDbType.Uuid, child));
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
