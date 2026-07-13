using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Covers the Phase 3 exit criteria (docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md
/// §10) that <see cref="GedcomStagingLifecycleTests"/> and
/// <see cref="GedcomImportPreviewTests"/> do not already exercise end-to-end:
/// <list type="bullet">
/// <item>staging the same fixture twice produces byte-identical staging rows
/// (deterministic tree-scoped person UUIDs);</item>
/// <item>staging never writes to any production (<c>genealogy.person</c>,
/// <c>family</c>, <c>event</c>, <c>person_name</c>, <c>parent_child</c>)
/// table;</item>
/// <item>previewing a full import into a genuinely empty tree classifies every
/// change as ADD, and those ADD counts equal independently-queried staging row
/// counts;</item>
/// <item>malformed input never leaves an APPLIED-looking batch behind.</item>
/// </list>
///
/// Each test manages its own database(s) directly (rather than one
/// per-class-instance database via <c>IAsyncLifetime</c>, as
/// <c>GedcomStagingLifecycleTests</c> does) because the repeatability test
/// needs two independent, identically tree-scoped databases while the other
/// three need exactly one. All four still use the same primitives as the
/// mirrored tests: <see cref="TestSeeding.NewTestDatabaseName"/>,
/// <see cref="WorkspaceEnvironmentFixture.CreateDatabaseAsync"/>,
/// <see cref="MigrationEngine.Migrate"/>, and
/// <see cref="WorkspaceEnvironmentFixture.DropDatabaseAsync"/>.
/// </summary>
public sealed class GedcomStagingExitCriteriaTests : IClassFixture<WorkspaceEnvironmentFixture>
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private readonly string _fixtureGedcomPath;

    public GedcomStagingExitCriteriaTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
        _fixtureGedcomPath = Path.Combine(
            _fixture.WorkspaceDirectory, "tools", "gedcom", "tests", "fixtures", "phase0_baseline.ged");
        Assert.True(File.Exists(_fixtureGedcomPath), $"Fixture not found: {_fixtureGedcomPath}");
    }

    [Fact]
    public async Task Stage_SameFixtureTwice_ProducesIdenticalStagingRows()
    {
        // Person UUIDs are derived from (xref, tree_id) via TreeUuidScope in
        // gedcom_tool.py, so staging the identical fixture under the identical
        // tree id in two independent databases must yield the identical set of
        // tree_person_id values and identical per-table row counts.
        var sharedTreeId = Guid.NewGuid();

        var envA = await CreateEnvironmentAsync();
        var envB = await CreateEnvironmentAsync();
        try
        {
            await SeedTreeAsync(envA, sharedTreeId, "Repeatability Tree A");
            await SeedTreeAsync(envB, sharedTreeId, "Repeatability Tree B");

            var serviceA = new GedcomStagingService(envA.ConnectionFactory);
            var serviceB = new GedcomStagingService(envB.ConnectionFactory);

            var resultA = await serviceA.StageAsync(
                new GedcomStageRequest { GedcomFilePath = _fixtureGedcomPath, TreeId = sharedTreeId });
            var resultB = await serviceB.StageAsync(
                new GedcomStageRequest { GedcomFilePath = _fixtureGedcomPath, TreeId = sharedTreeId });

            // Per-table row counts are stable across runs.
            var countsA = resultA.RowCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var countsB = resultB.RowCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            Assert.Equal(countsA, countsB);
            Assert.Equal(28, resultA.RowCounts["genealogy.gedcom_import_person"]);

            // The set of tree_person_id values is byte-identical (order-independent).
            var personIdsA = await GetDistinctPersonIdsAsync(envA, resultA.BatchId);
            var personIdsB = await GetDistinctPersonIdsAsync(envB, resultB.BatchId);
            Assert.Equal(personIdsA.OrderBy(g => g).ToList(), personIdsB.OrderBy(g => g).ToList());

            // Same for family ids, which are also derived deterministically from
            // (xref, tree_id).
            var familyIdsA = await GetDistinctFamilyIdsAsync(envA, resultA.BatchId);
            var familyIdsB = await GetDistinctFamilyIdsAsync(envB, resultB.BatchId);
            Assert.Equal(familyIdsA.OrderBy(g => g).ToList(), familyIdsB.OrderBy(g => g).ToList());
        }
        finally
        {
            await DropEnvironmentAsync(envA);
            await DropEnvironmentAsync(envB);
        }
    }

    [Fact]
    public async Task Stage_DoesNotModifyProductionTables()
    {
        var env = await CreateEnvironmentAsync();
        try
        {
            var treeId = Guid.NewGuid();
            await SeedTreeAsync(env, treeId, "Production Untouched Tree");

            var p1 = Guid.NewGuid();
            var p2 = Guid.NewGuid();

            await using (var connection = new NpgsqlConnection(env.ConnectionString))
            {
                await connection.OpenAsync();

                await TestSeeding.InsertPersonAsync(connection, treeId, p1,
                    externalId: "PX1", primaryDisplayName: "Prod Person One", surnameNormalized: "one",
                    sex: 'M', isLiving: false);
                await TestSeeding.InsertPersonAsync(connection, treeId, p2,
                    externalId: "PX2", primaryDisplayName: "Prod Person Two", surnameNormalized: "two",
                    sex: 'F', isLiving: true);
                await TestSeeding.InsertPersonNameAsync(connection, treeId, p1,
                    scriptCode: "Latn", nameType: "Primary",
                    fullName: "Prod Person One", fullNameNormalized: "prod person one", isPrimary: true);
                await TestSeeding.InsertFamilyAsync(connection, treeId, Guid.NewGuid(), p1, p2, marriageYear: 1950);
                await TestSeeding.InsertParentChildAsync(connection, treeId, p1, p2);
                await InsertProdEventAsync(connection, treeId, p1, "BIRT");
            }

            var before = await SnapshotProductionCountsAsync(env);

            var service = new GedcomStagingService(env.ConnectionFactory);
            await service.StageAsync(
                new GedcomStageRequest { GedcomFilePath = _fixtureGedcomPath, TreeId = treeId });

            var after = await SnapshotProductionCountsAsync(env);

            // Staging only ever writes to genealogy.gedcom_import_* tables; every
            // production table's row count is unchanged.
            Assert.Equal(before, after);
        }
        finally
        {
            await DropEnvironmentAsync(env);
        }
    }

    [Fact]
    public async Task Preview_FullFixtureIntoEmptyTree_AllChangesAreAdd()
    {
        var env = await CreateEnvironmentAsync();
        try
        {
            var treeId = Guid.NewGuid();
            await SeedTreeAsync(env, treeId, "Empty Tree For Preview");

            var stagingService = new GedcomStagingService(env.ConnectionFactory);
            var staged = await stagingService.StageAsync(
                new GedcomStageRequest { GedcomFilePath = _fixtureGedcomPath, TreeId = treeId });

            var previewService = new GedcomImportPreviewService(env.ConnectionFactory);
            var preview = await previewService.PreviewAsync(staged.BatchId);

            // Production is empty, so no comparison can ever produce UPDATE or
            // MISSING_FROM_IMPORT: every reported change must be ADD.
            Assert.NotEmpty(preview.Changes);
            Assert.All(preview.Changes, c => Assert.Equal("ADD", c.ChangeType));

            long CountOf(string entity) =>
                preview.Changes.SingleOrDefault(c => c.EntityType == entity)?.RowCount ?? 0;

            // Entities whose preview-ADD join has no extra filter beyond "no
            // matching production row" reduce, against an empty tree, to a plain
            // staging row count -- so the preview ADD count must equal the
            // independently-queried staging row count for each.
            Assert.Equal(staged.RowCounts["genealogy.gedcom_import_person"], CountOf("Person"));
            Assert.Equal(staged.RowCounts["genealogy.gedcom_import_person_name"], CountOf("PersonName"));
            Assert.Equal(staged.RowCounts["genealogy.gedcom_import_place"], CountOf("Place"));
            Assert.Equal(staged.RowCounts["genealogy.gedcom_import_parent_of"], CountOf("ParentOf"));
            Assert.Equal(staged.RowCounts["genealogy.gedcom_import_family_child"], CountOf("FamilyChild"));
            Assert.Equal(staged.RowCounts["genealogy.gedcom_import_event"], CountOf("Event"));

            await using var connection = new NpgsqlConnection(env.ConnectionString);
            await connection.OpenAsync();

            // Family ADD additionally requires both spouses to have resolved
            // (migration 0005: "s.spouse1_tree_person_id is not null and
            // s.spouse2_tree_person_id is not null"), so its independently-queried
            // baseline is a filtered staging count, not the full staged row count.
            var familyAddExpected = await ScalarLongAsync(connection,
                """
                SELECT count(*) FROM genealogy.gedcom_import_family
                WHERE import_batch_id = @batch_id
                  AND spouse1_tree_person_id IS NOT NULL
                  AND spouse2_tree_person_id IS NOT NULL;
                """, staged.BatchId);
            Assert.Equal(familyAddExpected, CountOf("Family"));

            // SpouseOf ADD: with an empty tree there is no genealogy.family row to
            // match against, so every staged spouse edge is new.
            var spouseOfExpected = await ScalarLongAsync(connection,
                "SELECT count(*) FROM genealogy.gedcom_import_spouse_of WHERE import_batch_id = @batch_id;",
                staged.BatchId);
            Assert.Equal(spouseOfExpected, CountOf("SpouseOf"));

            // EventCitation ADD: with no production events, every staged citation
            // whose event row exists is an ADD (never REPLACE).
            var citationExpected = await ScalarLongAsync(connection,
                """
                SELECT count(*)
                FROM genealogy.gedcom_import_event_citation c
                JOIN genealogy.gedcom_import_event s
                    ON s.import_batch_id = c.import_batch_id AND s.row_number = c.event_row_number
                WHERE c.import_batch_id = @batch_id;
                """, staged.BatchId);
            Assert.Equal(citationExpected, CountOf("EventCitation"));
        }
        finally
        {
            await DropEnvironmentAsync(env);
        }
    }

    [Fact]
    public async Task Stage_MalformedInput_LeavesNoApplyableBatch()
    {
        var env = await CreateEnvironmentAsync();
        try
        {
            var treeId = Guid.NewGuid();
            await SeedTreeAsync(env, treeId, "Malformed Input Tree");
            var service = new GedcomStagingService(env.ConnectionFactory);

            // --- (a) Nonexistent GEDCOM path -------------------------------
            var missingPath = Path.Combine(
                Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".ged");

            await Assert.ThrowsAsync<FileNotFoundException>(() => service.StageAsync(
                new GedcomStageRequest { GedcomFilePath = missingPath, TreeId = treeId }));

            Assert.Equal(0, await CountBatchesAsync(env));

            // --- (b) Genuinely malformed .ged content ----------------------
            // gedcom_tool.py's line parser (parse_gedcom) matches every line
            // against `^(\d+)\s+(?:(@[^@]+@)\s+)?([A-Z0-9_]+)(?:\s+(.*))?$` and
            // silently `continue`s past anything that doesn't match -- it never
            // raises on unrecognized content, and read_text_guess_encoding
            // decodes with errors="replace" so it never raises on bad bytes
            // either. None of the lines below start with "<digit> <TAG>", so
            // none match: the exporter parses this as a well-formed but EMPTY
            // GEDCOM (zero INDI/FAM records) and exits 0.
            var malformedPath = Path.Combine(
                Path.GetTempPath(), "malformed-" + Guid.NewGuid().ToString("N") + ".ged");
            await File.WriteAllTextAsync(malformedPath, """
                This is not a valid GEDCOM file.
                It has no level-tag structure whatsoever.
                ???  %%%  !!!  <<< completely broken input >>>
                garbage garbage garbage
                """);

            try
            {
                GedcomStagingLoadResult? loadResult = null;
                Exception? thrown = null;
                try
                {
                    loadResult = await service.StageAsync(
                        new GedcomStageRequest { GedcomFilePath = malformedPath, TreeId = treeId });
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                if (thrown is not null)
                {
                    // BRANCH ACTUALLY HIT: THROW. Confirm no batch row was left
                    // behind for this malformed input (in addition to whatever
                    // batch subcase (a) may or may not have left -- it left none).
                    Assert.Equal(0, await CountBatchesAsync(env));
                }
                else
                {
                    // BRANCH ACTUALLY HIT: LENIENT LOAD. The parser treated the
                    // garbage text as a valid, empty GEDCOM. Prove the safety
                    // invariant instead of the throw: the resulting batch is a
                    // single, internally consistent STAGED row -- never APPLIED,
                    // and never a half-written state (every staging table's
                    // loaded row count matches the batch header's own counts,
                    // all zero).
                    Assert.NotNull(loadResult);
                    Assert.Equal(1, await CountBatchesAsync(env));

                    await using var connection = new NpgsqlConnection(env.ConnectionString);
                    await connection.OpenAsync();
                    await using var command = new NpgsqlCommand(
                        """
                        SELECT status, person_count, family_count, event_count, place_count
                        FROM genealogy.gedcom_import_batch
                        WHERE import_batch_id = @batch_id;
                        """, connection);
                    command.Parameters.Add(new NpgsqlParameter("batch_id", NpgsqlDbType.Uuid) { Value = loadResult!.BatchId });

                    await using var reader = await command.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());

                    var status = reader.GetString(0);
                    Assert.NotEqual("APPLIED", status);
                    Assert.Equal("STAGED", status);

                    Assert.Equal(loadResult.RowCounts["genealogy.gedcom_import_person"], reader.GetInt32(1));
                    Assert.Equal(loadResult.RowCounts["genealogy.gedcom_import_family"], reader.GetInt32(2));
                    Assert.Equal(loadResult.RowCounts["genealogy.gedcom_import_event"], reader.GetInt32(3));
                    Assert.Equal(loadResult.RowCounts["genealogy.gedcom_import_place"], reader.GetInt32(4));

                    // The parser found nothing resembling a person or family in
                    // the malformed content.
                    Assert.Equal(0L, loadResult.RowCounts["genealogy.gedcom_import_person"]);
                    Assert.Equal(0L, loadResult.RowCounts["genealogy.gedcom_import_family"]);
                }
            }
            finally
            {
                File.Delete(malformedPath);
            }
        }
        finally
        {
            await DropEnvironmentAsync(env);
        }
    }

    // --- Environment plumbing (per-test database create/migrate/drop) ---

    private sealed record TestEnvironment(
        string DatabaseName, string ConnectionString, NpgsqlConnectionFactory ConnectionFactory);

    private async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(databaseName);
        var connectionString = NpgsqlConnectionFactory.BuildConnectionString(
            _fixture.BuildOptionsForDatabase(databaseName));

        new MigrationEngine(connectionString).Migrate();

        var connectionFactory = new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(databaseName));
        return new TestEnvironment(databaseName, connectionString, connectionFactory);
    }

    private async Task DropEnvironmentAsync(TestEnvironment env) =>
        await _fixture.DropDatabaseAsync(env.DatabaseName);

    private static async Task SeedTreeAsync(TestEnvironment env, Guid treeId, string name)
    {
        await using var connection = new NpgsqlConnection(env.ConnectionString);
        await connection.OpenAsync();
        await TestSeeding.InsertTreeAsync(connection, treeId, name);
    }

    private static async Task<List<Guid>> GetDistinctPersonIdsAsync(TestEnvironment env, Guid batchId)
    {
        await using var connection = new NpgsqlConnection(env.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT tree_person_id FROM genealogy.gedcom_import_person WHERE import_batch_id = @batch_id;",
            connection);
        command.Parameters.Add(new NpgsqlParameter("batch_id", NpgsqlDbType.Uuid) { Value = batchId });

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<List<Guid>> GetDistinctFamilyIdsAsync(TestEnvironment env, Guid batchId)
    {
        await using var connection = new NpgsqlConnection(env.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT family_id FROM genealogy.gedcom_import_family WHERE import_batch_id = @batch_id;",
            connection);
        command.Parameters.Add(new NpgsqlParameter("batch_id", NpgsqlDbType.Uuid) { Value = batchId });

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private sealed record ProductionCounts(long Person, long Family, long Event, long PersonName, long ParentChild);

    private static async Task<ProductionCounts> SnapshotProductionCountsAsync(TestEnvironment env)
    {
        await using var connection = new NpgsqlConnection(env.ConnectionString);
        await connection.OpenAsync();

        return new ProductionCounts(
            Person: await ScalarLongAsync(connection, "SELECT count(*) FROM genealogy.person;"),
            Family: await ScalarLongAsync(connection, "SELECT count(*) FROM genealogy.family;"),
            Event: await ScalarLongAsync(connection, "SELECT count(*) FROM genealogy.event;"),
            PersonName: await ScalarLongAsync(connection, "SELECT count(*) FROM genealogy.person_name;"),
            ParentChild: await ScalarLongAsync(connection, "SELECT count(*) FROM genealogy.parent_child;"));
    }

    private static async Task<long> CountBatchesAsync(TestEnvironment env)
    {
        await using var connection = new NpgsqlConnection(env.ConnectionString);
        await connection.OpenAsync();
        return await ScalarLongAsync(connection, "SELECT count(*) FROM genealogy.gedcom_import_batch;");
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, Guid batchId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("batch_id", NpgsqlDbType.Uuid) { Value = batchId });
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>
    /// Raw production-event insert, kept local (mirroring
    /// <c>GedcomImportPreviewTests.InsertProdEventAsync</c>) since
    /// <see cref="TestSeeding"/> does not yet expose a write path for
    /// <c>genealogy.event</c>.
    /// </summary>
    private static async Task InsertProdEventAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, string eventType)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO genealogy.event (tree_id, person_id, event_type, is_derived)
            VALUES (@tree_id, @person_id, @event_type, false);
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Varchar) { Value = eventType });
        await command.ExecuteNonQueryAsync();
    }
}
