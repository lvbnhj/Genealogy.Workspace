using Genealogy.Workspace.Data;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 7, Task 2 exit criterion: an upgrade from an earlier migration set to
/// the current one preserves prior content. This is the PREFERRED approach
/// described in the plan (rather than the "re-run Migrate() as an idempotent
/// no-op" fallback) because <see cref="MigrationEngine"/>'s constructor takes
/// an explicit <c>migrationsDirectory</c> parameter (see its second
/// constructor argument), so a genuinely earlier release's schema can be
/// reproduced by pointing a first engine at a temp directory containing only
/// migrations 0001-0008, then pointing a second engine at the full migrations
/// directory (which also contains 0009_research_evidence_schema.sql) so only
/// the new migration applies.
///
/// Mirrors <see cref="DatabaseLifecycleTests"/> and <see cref="ResearchSchemaTests"/>:
/// a fresh, uniquely named database per test, dropped in <see cref="DisposeAsync"/>.
/// </summary>
public sealed class UpgradePreservesContentTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private string? _previousReleaseMigrationsDir;

    public UpgradePreservesContentTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(
            _fixture.BuildOptionsForDatabase(_databaseName));
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);

        if (_previousReleaseMigrationsDir is not null && Directory.Exists(_previousReleaseMigrationsDir))
        {
            Directory.Delete(_previousReleaseMigrationsDir, recursive: true);
        }
    }

    [Fact]
    public async Task Upgrade_From0001To0008_Then0009_PreservesPriorContent_AndUnlocksResearchSchema()
    {
        // ---------------------------------------------------------------
        // Step 1: Discover the repo's full migrations directory (via a
        // throwaway engine's default-directory resolution — constructing it
        // only resolves the path, it does not touch the database), then copy
        // migrations 0001-0008 into a temp dir to stand in for "the schema as
        // it existed in an earlier release" (before 0009 was added).
        // ---------------------------------------------------------------
        var fullMigrationsDir = new MigrationEngine(_connectionString).MigrationsDirectory;

        _previousReleaseMigrationsDir = Directory.CreateTempSubdirectory("gw_previous_release_").FullName;
        foreach (var file in Directory.GetFiles(fullMigrationsDir, "*.sql"))
        {
            var name = Path.GetFileName(file);
            if (TryGetNumericPrefix(name, out var prefix) && prefix <= 8)
            {
                File.Copy(file, Path.Combine(_previousReleaseMigrationsDir, name));
            }
        }

        Assert.True(
            Directory.GetFiles(_previousReleaseMigrationsDir, "*.sql").Length == 8,
            "Expected exactly migrations 0001-0008 to be copied into the previous-release directory.");

        // ---------------------------------------------------------------
        // Step 2: Apply the "previous release" schema (0001-0008 only).
        // ---------------------------------------------------------------
        var previousReleaseEngine = new MigrationEngine(_connectionString, _previousReleaseMigrationsDir);
        var appliedByPreviousRelease = previousReleaseEngine.Migrate();

        Assert.Contains(appliedByPreviousRelease, n => n.Contains("0008_gedcom_apply", StringComparison.Ordinal));
        Assert.DoesNotContain(appliedByPreviousRelease, n => n.Contains("0009_research_evidence_schema", StringComparison.Ordinal));

        var statusAfterPreviousRelease = previousReleaseEngine.Status();
        Assert.Empty(statusAfterPreviousRelease.Pending);

        // ---------------------------------------------------------------
        // Step 3: Seed pre-upgrade content: a genealogy.tree + genealogy.person
        // (production tables from 0002) and a genealogy.gedcom_import_batch
        // staged row (staging table from 0004), so the test proves both
        // "normal" tree content and staging-import content survive.
        // ---------------------------------------------------------------
        var treeId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var importBatchId = Guid.NewGuid();
        const string treeName = "Pre-Upgrade Tree";
        const string personDisplayName = "Pre-Upgrade Person";
        const string batchSourcePath = "/imports/pre-upgrade.ged";

        await using (var seedConnection = new NpgsqlConnection(_connectionString))
        {
            await seedConnection.OpenAsync();

            await TestSeeding.InsertTreeAsync(seedConnection, treeId, treeName);
            await TestSeeding.InsertPersonAsync(
                seedConnection, treeId, personId, primaryDisplayName: personDisplayName, surnameNormalized: "upgrade");
            await InsertStagedGedcomImportBatchAsync(seedConnection, importBatchId, treeId, batchSourcePath);
        }

        // research.* does not exist yet at this point (only created by 0009):
        // confirm the upgrade is genuinely needed to unlock it.
        await using (var preUpgradeConnection = new NpgsqlConnection(_connectionString))
        {
            await preUpgradeConnection.OpenAsync();
            Assert.False(await SchemaHasTableAsync(preUpgradeConnection, "research", "source_record"));
        }

        // ---------------------------------------------------------------
        // Step 4: Upgrade — point a new engine at the FULL migrations
        // directory. Migrations 0001-0008 are already journaled (by name) so
        // only 0009_research_evidence_schema.sql should apply.
        // ---------------------------------------------------------------
        var currentReleaseEngine = new MigrationEngine(_connectionString, fullMigrationsDir);
        var appliedByUpgrade = currentReleaseEngine.Migrate();

        Assert.Single(appliedByUpgrade);
        Assert.Contains(appliedByUpgrade, n => n.Contains("0009_research_evidence_schema", StringComparison.Ordinal));

        var statusAfterUpgrade = currentReleaseEngine.Status();
        Assert.Empty(statusAfterUpgrade.Pending);
        Assert.Equal(9, statusAfterUpgrade.Applied.Count);

        // ---------------------------------------------------------------
        // Step 5 (assertion a): the pre-upgrade rows still exist, unchanged.
        // ---------------------------------------------------------------
        await using (var postUpgradeConnection = new NpgsqlConnection(_connectionString))
        {
            await postUpgradeConnection.OpenAsync();

            var readBackTreeName = await ScalarTextAsync(
                postUpgradeConnection,
                "select name from genealogy.tree where tree_id = @tree_id;",
                ("tree_id", NpgsqlDbType.Uuid, treeId));
            Assert.Equal(treeName, readBackTreeName);

            var readBackPersonName = await ScalarTextAsync(
                postUpgradeConnection,
                "select primary_display_name from genealogy.person where person_id = @person_id;",
                ("person_id", NpgsqlDbType.Uuid, personId));
            Assert.Equal(personDisplayName, readBackPersonName);

            var readBackBatchStatus = await ScalarTextAsync(
                postUpgradeConnection,
                "select status from genealogy.gedcom_import_batch where import_batch_id = @import_batch_id;",
                ("import_batch_id", NpgsqlDbType.Uuid, importBatchId));
            Assert.Equal("STAGED", readBackBatchStatus);

            var readBackBatchSourcePath = await ScalarTextAsync(
                postUpgradeConnection,
                "select source_file_path from genealogy.gedcom_import_batch where import_batch_id = @import_batch_id;",
                ("import_batch_id", NpgsqlDbType.Uuid, importBatchId));
            Assert.Equal(batchSourcePath, readBackBatchSourcePath);
        }

        // ---------------------------------------------------------------
        // Step 5 (assertion b): the newly-added research.source_record table
        // (created only by 0009) is now usable — insert and read back a row.
        // ---------------------------------------------------------------
        await using (var researchConnection = new NpgsqlConnection(_connectionString))
        {
            await researchConnection.OpenAsync();

            Assert.True(await SchemaHasTableAsync(researchConnection, "research", "source_record"));

            var sourceRecordId = Guid.NewGuid();
            const string title = "Post-upgrade evidence record";

            await using (var insertCommand = new NpgsqlCommand(
                """
                INSERT INTO research.source_record (source_record_id, tree_id, title, record_type)
                VALUES (@source_record_id, @tree_id, @title, @record_type);
                """, researchConnection))
            {
                insertCommand.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
                insertCommand.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
                insertCommand.Parameters.Add(new NpgsqlParameter("title", NpgsqlDbType.Text) { Value = title });
                insertCommand.Parameters.Add(new NpgsqlParameter("record_type", NpgsqlDbType.Text) { Value = "birth" });
                await insertCommand.ExecuteNonQueryAsync();
            }

            var readBackTitle = await ScalarTextAsync(
                researchConnection,
                "select title from research.source_record where source_record_id = @source_record_id;",
                ("source_record_id", NpgsqlDbType.Uuid, sourceRecordId));
            Assert.Equal(title, readBackTitle);
        }
    }

    private static async Task InsertStagedGedcomImportBatchAsync(
        NpgsqlConnection connection, Guid importBatchId, Guid treeId, string sourceFilePath)
    {
        const string sql = """
            INSERT INTO genealogy.gedcom_import_batch (import_batch_id, source_file_path, tree_id, status)
            VALUES (@import_batch_id, @source_file_path, @tree_id, 'STAGED');
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });
        command.Parameters.Add(new NpgsqlParameter("source_file_path", NpgsqlDbType.Text) { Value = sourceFilePath });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> SchemaHasTableAsync(NpgsqlConnection connection, string schemaName, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*) from information_schema.tables
            where table_schema = @schema and table_name = @table;
            """;
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("table", tableName);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        return count > 0;
    }

    private static async Task<string?> ScalarTextAsync(
        NpgsqlConnection connection, string sql, (string Name, NpgsqlDbType Type, object Value) parameter)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Type) { Value = parameter.Value });
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    private static bool TryGetNumericPrefix(string fileName, out int prefix)
    {
        var digits = fileName.TakeWhile(char.IsDigit).ToArray();
        if (digits.Length > 0 && int.TryParse(new string(digits), out prefix))
        {
            return true;
        }

        prefix = 0;
        return false;
    }
}
