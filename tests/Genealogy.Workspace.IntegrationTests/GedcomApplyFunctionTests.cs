using Genealogy.Workspace.Data;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 4 exit criterion for the GEDCOM import APPLY port: exercises the
/// <c>genealogy.apply_gedcom_import(uuid, boolean)</c> function (migration 0008)
/// directly via SQL against small hand-built scenarios seeded straight into the
/// <c>gedcom_import_*</c> staging tables (no dependency on the artifact loader or
/// the .NET apply service, both owned by parallel tasks).
///
/// Coverage:
///   * happy path -- a staged batch of persons/names/places/families/children/
///     parent edges/events/citations lands in production and the batch flips to
///     APPLIED;
///   * idempotency -- resetting the batch status and re-applying the same
///     deterministic data adds zero net production rows;
///   * delete_missing -- a production person absent from staging is deleted only
///     when p_delete_missing = true, and the root-missing safety valve refuses;
///   * atomic rollback -- a failure mid-apply (a staged person whose sex trips
///     the production CHECK) leaves NO partial production rows behind.
/// </summary>
public sealed class GedcomApplyFunctionTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    private readonly Guid _tree = Guid.NewGuid();
    private readonly Guid _batch = Guid.NewGuid();
    private readonly Guid _p1 = Guid.NewGuid();   // spouse 1 + batch root
    private readonly Guid _p2 = Guid.NewGuid();   // spouse 2
    private readonly Guid _p3 = Guid.NewGuid();   // child
    private readonly Guid _fam = Guid.NewGuid();

    private const string PlaceRaw = "Kyiv, Ukraine";

    public GedcomApplyFunctionTests(WorkspaceEnvironmentFixture fixture)
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

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Production tree only; everything else arrives through staging + apply.
        await TestSeeding.InsertTreeAsync(connection, _tree, "Apply Test Tree");

        // Staged batch (root = P1) ready to apply.
        await InsertBatchAsync(connection, _batch, _tree, _p1, "WAITING_FOR_CONFIRMATION");

        await InsertStagingPersonAsync(connection, _batch, _p1, "I1", 'M', false, "John Doe", "doe");
        await InsertStagingPersonAsync(connection, _batch, _p2, "I2", 'F', true, "Jane Doe", "doe");
        await InsertStagingPersonAsync(connection, _batch, _p3, "I3", 'M', null, "Kid Doe", "doe");

        // Two names for P1 (one primary, one AKA); one primary each for P2/P3.
        await InsertStagingNameAsync(connection, _batch, 1, _p1, "John Doe", "john doe", true, "John");
        await InsertStagingNameAsync(connection, _batch, 2, _p1, "Johnny Doe", "johnny doe", false, "Johnny");
        await InsertStagingNameAsync(connection, _batch, 3, _p2, "Jane Doe", "jane doe", true, "Jane");
        await InsertStagingNameAsync(connection, _batch, 4, _p3, "Kid Doe", "kid doe", true, "Kid");

        await InsertStagingPlaceAsync(connection, _batch, 1, PlaceRaw, "kyiv ukraine");

        await InsertStagingFamilyAsync(connection, _batch, _fam, _p1, _p2, 1950, PlaceRaw);
        await InsertStagingFamilyChildAsync(connection, _batch, _fam, _p3);

        await InsertStagingParentOfAsync(connection, _batch, _p1, _p3, "BIO");
        await InsertStagingParentOfAsync(connection, _batch, _p2, _p3, "BIO");

        // One event on P1 at the staged place, plus a citation on it.
        await InsertStagingEventAsync(connection, _batch, 1, "I1-BIRT-1", _p1, "BIRT", PlaceRaw);
        await InsertStagingCitationAsync(connection, _batch, 1, 1, "SRC-1");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task Apply_HappyPath_WritesProductionRows_AndFlipsStatus()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var summary = await ApplyAsync(connection, _batch, deleteMissing: false);

        // Per-entity ADD summary reflects exactly what was staged.
        Assert.Equal(1, summary["Place/ADD"]);
        Assert.Equal(3, summary["Person/ADD"]);
        Assert.Equal(4, summary["PersonName/ADD"]);
        Assert.Equal(1, summary["Family/ADD"]);
        Assert.Equal(1, summary["FamilyChild/ADD"]);
        Assert.Equal(2, summary["ParentOf/ADD"]);
        Assert.Equal(1, summary["Event/ADD"]);
        Assert.Equal(1, summary["EventCitation/REPLACE"]);
        // No UPDATE/DELETE rows on a first apply into an empty tree.
        Assert.DoesNotContain("Person/UPDATE", summary.Keys);
        Assert.DoesNotContain("Person/DELETE", summary.Keys);

        // Production tables now hold the applied rows.
        Assert.Equal(3, await ScalarAsync(connection, "select count(*) from genealogy.person"));
        Assert.Equal(4, await ScalarAsync(connection, "select count(*) from genealogy.person_name"));
        Assert.Equal(1, await ScalarAsync(connection, "select count(*) from genealogy.place"));
        Assert.Equal(1, await ScalarAsync(connection, "select count(*) from genealogy.family"));
        Assert.Equal(1, await ScalarAsync(connection, "select count(*) from genealogy.family_child"));
        Assert.Equal(2, await ScalarAsync(connection, "select count(*) from genealogy.parent_child"));
        Assert.Equal(1, await ScalarAsync(connection, "select count(*) from genealogy.event"));
        Assert.Equal(1, await ScalarAsync(connection, "select count(*) from genealogy.event_citation"));

        // Exactly one primary name for P1 (the one-primary index is respected).
        Assert.Equal(1, await ScalarAsync(connection,
            $"select count(*) from genealogy.person_name where person_id = '{_p1}' and is_primary"));

        // The event resolved its place and carries a GEDCOM citation.
        Assert.Equal(1, await ScalarAsync(connection,
            "select count(*) from genealogy.event where place_id is not null and external_event_key = 'I1-BIRT-1'"));
        Assert.Equal(1, await ScalarAsync(connection,
            "select count(*) from genealogy.event_citation where source_origin = 'GEDCOM'"));

        // Batch status flipped to APPLIED with an applied_at timestamp; tree root set.
        Assert.Equal("APPLIED", await ScalarStringAsync(connection,
            $"select status from genealogy.gedcom_import_batch where import_batch_id = '{_batch}'"));
        Assert.Equal(1, await ScalarAsync(connection,
            $"select count(*) from genealogy.gedcom_import_batch where import_batch_id = '{_batch}' and applied_at is not null"));
        Assert.Equal(_p1.ToString(), await ScalarStringAsync(connection,
            $"select root_person_id::text from genealogy.tree where tree_id = '{_tree}'"));
    }

    [Fact]
    public async Task Apply_Twice_IsIdempotent_NoNetNewRows()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await ApplyAsync(connection, _batch, deleteMissing: false);

        var before = await SnapshotCountsAsync(connection);

        // A second apply of the same batch is blocked by the status guard, so
        // idempotency is exercised by resetting the status and re-applying the
        // same deterministic data.
        await ExecuteAsync(connection,
            "update genealogy.gedcom_import_batch set status = 'WAITING_FOR_CONFIRMATION' where import_batch_id = @b;",
            ("b", NpgsqlDbType.Uuid, _batch));

        var summary = await ApplyAsync(connection, _batch, deleteMissing: false);
        var after = await SnapshotCountsAsync(connection);

        // Every production table has the same row count as after the first apply.
        Assert.Equal(before, after);

        // The only churn the second apply is allowed is the GEDCOM citation
        // delete+reinsert (same count); nothing is added, updated or deleted.
        Assert.DoesNotContain("Person/ADD", summary.Keys);
        Assert.DoesNotContain("Person/UPDATE", summary.Keys);
        Assert.DoesNotContain("Family/ADD", summary.Keys);
        Assert.DoesNotContain("Event/ADD", summary.Keys);
        Assert.DoesNotContain("PersonName/ADD", summary.Keys);
        foreach (var key in summary.Keys)
        {
            Assert.EndsWith("/REPLACE", key);
        }
    }

    [Fact]
    public async Task Apply_DeleteMissingFalse_KeepsGhost_True_DeletesIt()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // A production person (and name) that is NOT present in staging.
        var ghost = Guid.NewGuid();
        await TestSeeding.InsertPersonAsync(connection, _tree, ghost,
            externalId: "IGH", primaryDisplayName: "Ghost", surnameNormalized: "ghost");
        await TestSeeding.InsertPersonNameAsync(connection, _tree, ghost,
            "Latn", "BIRTH", "Ghost", "ghost", isPrimary: true);

        // delete_missing = false must never delete anything.
        await ApplyAsync(connection, _batch, deleteMissing: false);
        Assert.Equal(1, await ScalarAsync(connection,
            $"select count(*) from genealogy.person where person_id = '{ghost}'"));

        // Re-run with delete_missing = true; the ghost (and its name) are removed.
        await ExecuteAsync(connection,
            "update genealogy.gedcom_import_batch set status = 'WAITING_FOR_CONFIRMATION' where import_batch_id = @b;",
            ("b", NpgsqlDbType.Uuid, _batch));

        var summary = await ApplyAsync(connection, _batch, deleteMissing: true);

        Assert.Equal(1, summary["Person/DELETE"]);
        Assert.Equal(1, summary["PersonName/DELETE"]);
        Assert.Equal(0, await ScalarAsync(connection,
            $"select count(*) from genealogy.person where person_id = '{ghost}'"));
        // The staged persons are untouched by the delete.
        Assert.Equal(3, await ScalarAsync(connection, "select count(*) from genealogy.person"));
    }

    [Fact]
    public async Task Apply_DeleteMissing_RootMissingFromStaging_Raises()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Point the batch root at a person that is NOT staged, then arm the valve.
        await ExecuteAsync(connection,
            "update genealogy.gedcom_import_batch set root_person_id = @r where import_batch_id = @b;",
            ("r", NpgsqlDbType.Uuid, Guid.NewGuid()),
            ("b", NpgsqlDbType.Uuid, _batch));

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => ApplyAsync(connection, _batch, deleteMissing: true));

        Assert.Contains("root person", ex.MessageText, StringComparison.OrdinalIgnoreCase);
        // Nothing was applied: the batch is still awaiting confirmation.
        Assert.Equal("WAITING_FOR_CONFIRMATION", await ScalarStringAsync(connection,
            $"select status from genealogy.gedcom_import_batch where import_batch_id = '{_batch}'"));
    }

    [Fact]
    public async Task Apply_FailureMidApply_RollsBackEverything()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // A separate tree/batch whose person carries a sex value that passes the
        // permissive staging table but violates the production person CHECK
        // (sex in 'M','F'). The place is staged first (step 2) and inserted
        // before the failing person insert (step 3), so a successful rollback
        // must also undo the place.
        var badTree = Guid.NewGuid();
        var badBatch = Guid.NewGuid();
        var badPerson = Guid.NewGuid();
        const string badPlace = "RollbackVille";

        await TestSeeding.InsertTreeAsync(connection, badTree, "Rollback Tree");
        await InsertBatchAsync(connection, badBatch, badTree, null, "WAITING_FOR_CONFIRMATION");
        await InsertStagingPlaceAsync(connection, badBatch, 1, badPlace, null);
        await InsertStagingPersonAsync(connection, badBatch, badPerson, "IX", 'X', null, "Bad Sex", "bad");

        await Assert.ThrowsAsync<PostgresException>(
            () => ApplyAsync(connection, badBatch, deleteMissing: false));

        // The whole function is one atomic unit: neither the place nor the person
        // survives, and the batch never flips to APPLIED.
        Assert.Equal(0, await ScalarAsync(connection,
            $"select count(*) from genealogy.place where place_raw = '{badPlace}'"));
        Assert.Equal(0, await ScalarAsync(connection,
            $"select count(*) from genealogy.person where tree_id = '{badTree}'"));
        Assert.Equal("WAITING_FOR_CONFIRMATION", await ScalarStringAsync(connection,
            $"select status from genealogy.gedcom_import_batch where import_batch_id = '{badBatch}'"));
    }

    // --- apply + snapshot helpers ---

    private static async Task<Dictionary<string, long>> ApplyAsync(
        NpgsqlConnection connection, Guid batch, bool deleteMissing)
    {
        await using var command = new NpgsqlCommand(
            "select entity_type, change_type, row_count from genealogy.apply_gedcom_import(@b, @d);",
            connection);
        command.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = batch });
        command.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Boolean) { Value = deleteMissing });

        var result = new Dictionary<string, long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[$"{reader.GetString(0)}/{reader.GetString(1)}"] = reader.GetInt64(2);
        }

        return result;
    }

    private static async Task<Dictionary<string, long>> SnapshotCountsAsync(NpgsqlConnection connection)
    {
        var tables = new[]
        {
            "person", "person_name", "place", "family",
            "family_child", "parent_child", "event", "event_citation",
        };

        var counts = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            counts[table] = await ScalarAsync(connection, $"select count(*) from genealogy.{table}");
        }

        return counts;
    }

    // --- raw staging insert helpers (local, mirroring GedcomImportPreviewTests) ---

    private static async Task InsertBatchAsync(
        NpgsqlConnection connection, Guid batch, Guid tree, Guid? rootPersonId, string status)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_batch
                (import_batch_id, source_file_path, tree_id, root_person_id, status)
            VALUES (@b, @path, @tree, @root, @status);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("path", NpgsqlDbType.Text, "/tmp/apply-test.ged"),
            ("tree", NpgsqlDbType.Uuid, tree),
            ("root", NpgsqlDbType.Uuid, (object?)rootPersonId ?? DBNull.Value),
            ("status", NpgsqlDbType.Varchar, status));
    }

    private static async Task InsertStagingPersonAsync(
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

    private static async Task InsertStagingNameAsync(
        NpgsqlConnection connection, Guid batch, int rowNumber, Guid personId,
        string fullName, string fullNameNormalized, bool isPrimary, string? given)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person_name
                (import_batch_id, row_number, tree_person_id, script_code, name_type,
                 given, surname, full_name, full_name_normalized, is_primary)
            VALUES (@b, @row, @id, 'Latn', 'BIRTH', @given, 'Doe', @full, @norm, @primary);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, rowNumber),
            ("id", NpgsqlDbType.Uuid, personId),
            ("given", NpgsqlDbType.Text, (object?)given ?? DBNull.Value),
            ("full", NpgsqlDbType.Text, fullName),
            ("norm", NpgsqlDbType.Text, fullNameNormalized),
            ("primary", NpgsqlDbType.Boolean, isPrimary));
    }

    private static async Task InsertStagingPlaceAsync(
        NpgsqlConnection connection, Guid batch, int rowNumber, string placeRaw, string? placeNormalized)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_place (import_batch_id, row_number, place_raw, place_normalized)
            VALUES (@b, @row, @place, @norm);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, rowNumber),
            ("place", NpgsqlDbType.Text, placeRaw),
            ("norm", NpgsqlDbType.Text, (object?)placeNormalized ?? DBNull.Value));
    }

    private static async Task InsertStagingFamilyAsync(
        NpgsqlConnection connection, Guid batch, Guid familyId,
        Guid spouse1, Guid spouse2, short marriageYear, string marriagePlaceRaw)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_family
                (import_batch_id, family_id, spouse1_tree_person_id, spouse2_tree_person_id,
                 marriage_year, marriage_place_raw)
            VALUES (@b, @fam, @s1, @s2, @year, @place);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("fam", NpgsqlDbType.Uuid, familyId),
            ("s1", NpgsqlDbType.Uuid, spouse1),
            ("s2", NpgsqlDbType.Uuid, spouse2),
            ("year", NpgsqlDbType.Smallint, marriageYear),
            ("place", NpgsqlDbType.Text, marriagePlaceRaw));
    }

    private static async Task InsertStagingFamilyChildAsync(
        NpgsqlConnection connection, Guid batch, Guid familyId, Guid childPersonId)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_family_child (import_batch_id, family_id, child_tree_person_id)
            VALUES (@b, @fam, @child);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("fam", NpgsqlDbType.Uuid, familyId),
            ("child", NpgsqlDbType.Uuid, childPersonId));
    }

    private static async Task InsertStagingParentOfAsync(
        NpgsqlConnection connection, Guid batch, Guid parent, Guid child, string relationType)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_parent_of
                (import_batch_id, parent_tree_person_id, child_tree_person_id, relation_type)
            VALUES (@b, @parent, @child, @rel);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("parent", NpgsqlDbType.Uuid, parent),
            ("child", NpgsqlDbType.Uuid, child),
            ("rel", NpgsqlDbType.Varchar, relationType));
    }

    private static async Task InsertStagingEventAsync(
        NpgsqlConnection connection, Guid batch, int rowNumber, string externalKey,
        Guid personId, string eventType, string placeRaw)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event
                (import_batch_id, row_number, external_event_key, tree_person_id, event_type, place_raw, is_derived)
            VALUES (@b, @row, @key, @person, @type, @place, false);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, rowNumber),
            ("key", NpgsqlDbType.Varchar, externalKey),
            ("person", NpgsqlDbType.Uuid, personId),
            ("type", NpgsqlDbType.Varchar, eventType),
            ("place", NpgsqlDbType.Text, placeRaw));
    }

    private static async Task InsertStagingCitationAsync(
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

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : value.ToString();
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
