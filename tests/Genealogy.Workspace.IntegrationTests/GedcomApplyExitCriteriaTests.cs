using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 4 (Task F, final) END-TO-END exit criteria for the GEDCOM
/// duplicate-detection and guarded-apply vertical, exercised through the real
/// .NET services (<see cref="GedcomStagingService"/>,
/// <see cref="GedcomDuplicateService"/>, <see cref="GedcomApplyService"/>) with
/// on-disk GEDCOM fixtures parsed by the actual python tool — NOT hand-seeded
/// staging rows. This complements (and does not duplicate) the SQL-level and
/// hand-seeded-scenario coverage already provided by
/// <see cref="GedcomApplyFunctionTests"/>, <see cref="GedcomApplyServiceTests"/>,
/// <see cref="GedcomDuplicateScoringTests"/> and <see cref="GedcomDuplicateServiceTests"/>.
///
/// docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10 Phase 4 exit criteria
/// covered here:
/// <list type="bullet">
/// <item>"concurrent/double apply cannot create duplicate rows" — re-staging
/// and re-applying the same fixture under the same tree (deterministic
/// person/family ids) leaves production counts unchanged.</item>
/// <item>"duplicate fixtures reproduce agreed score bands" —
/// <c>dup_within_high_confidence.ged</c> staged and scored end-to-end produces
/// a within_import candidate at or above the 0.90 high-confidence band.</item>
/// <item>"negative evidence behavior" — <c>dup_within_negative_evidence.ged</c>
/// (same duplicate pair, but with a source-backed conflicting DEAT date pair)
/// drives the composite score below the 0.75 minimum, so the pair is never
/// even inserted as a suggested candidate.</item>
/// </list>
/// Also covers, lightly, the happy-path apply and the delete_missing branch
/// through the same public service surface.
/// </summary>
public sealed class GedcomApplyExitCriteriaTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private GedcomStagingService _stagingService = null!;
    private GedcomApplyService _applyService = null!;
    private GedcomDuplicateService _duplicateService = null!;

    public GedcomApplyExitCriteriaTests(WorkspaceEnvironmentFixture fixture)
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

        var connectionFactory = new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName));
        _stagingService = new GedcomStagingService(connectionFactory);
        _applyService = new GedcomApplyService(connectionFactory);
        _duplicateService = new GedcomDuplicateService(connectionFactory);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task ApplyAsync_EndToEnd_Phase0Baseline_IntoEmptyTree_ProducesMatchingProductionCounts()
    {
        var tree = Guid.NewGuid();
        await SeedTreeAsync(tree, "Apply Exit Criteria - Happy Path");

        var staged = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = FixturePath("phase0_baseline.ged"),
            TreeId = tree,
        });

        // Sanity: the real parser produced the fixture's documented shape
        // (mirrors GedcomStagingLifecycleTests' own assertions) before we lean
        // on RowCounts as the "expected" side of the production comparison.
        Assert.Equal(28, staged.RowCounts["genealogy.gedcom_import_person"]);
        Assert.Equal(8, staged.RowCounts["genealogy.gedcom_import_family"]);
        Assert.True(staged.RowCounts["genealogy.gedcom_import_event"] > 0);
        Assert.Equal(6, staged.RowCounts["genealogy.gedcom_import_place"]);

        var result = await _applyService.ApplyAsync(staged.BatchId, dryRun: false);

        Assert.False(result.DryRun);
        Assert.Equal("APPLIED", result.Status);

        await AssertProductionMatchesStagedAsync(tree, staged);
    }

    [Fact]
    public async Task ApplyAsync_ReStagingAndReApplyingSameFixtureUnderSameTree_DoesNotCreateDuplicateProductionRows()
    {
        var tree = Guid.NewGuid();
        await SeedTreeAsync(tree, "Apply Exit Criteria - Re-Apply Idempotency");

        var fixturePath = FixturePath("phase0_baseline.ged");

        var firstBatch = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = fixturePath,
            TreeId = tree,
        });
        var firstResult = await _applyService.ApplyAsync(firstBatch.BatchId, dryRun: false);
        Assert.Equal("APPLIED", firstResult.Status);

        var countsAfterFirstApply = await CaptureProductionCountsAsync(tree);

        // A second, independent staging of the SAME GEDCOM under the SAME
        // tree: deterministic (tree-scoped) UUIDv5 person/family ids mean the
        // second batch's staged rows resolve to the identical production
        // identities as the first, even though it is a brand-new batch_id.
        var secondBatch = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = fixturePath,
            TreeId = tree,
        });
        Assert.NotEqual(firstBatch.BatchId, secondBatch.BatchId);

        var secondResult = await _applyService.ApplyAsync(secondBatch.BatchId, dryRun: false);
        Assert.Equal("APPLIED", secondResult.Status);

        var countsAfterSecondApply = await CaptureProductionCountsAsync(tree);

        Assert.Equal(countsAfterFirstApply, countsAfterSecondApply);
    }

    [Fact]
    public async Task GenerateAsync_DupWithinHighConfidenceFixture_ProducesHighConfidenceWithinImportCandidate()
    {
        var tree = Guid.NewGuid();
        await SeedTreeAsync(tree, "Duplicate Exit Criteria - High Confidence");

        var staged = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = FixturePath("dup_within_high_confidence.ged"),
            TreeId = tree,
        });

        var inserted = await _duplicateService.GenerateAsync(staged.BatchId);
        Assert.True(inserted >= 1, "Expected at least one duplicate candidate to be generated.");

        var listed = await _duplicateService.ListCandidatesAsync(staged.BatchId);

        var withinImportSummary = Assert.Single(listed.Summary, s => s.CandidateScope == "within_import");
        Assert.True(
            withinImportSummary.HighConfidenceCount >= 1,
            $"Expected at least one high-confidence within_import candidate, summary was: {withinImportSummary}");

        var candidate = Assert.Single(listed.Candidates, c => c.CandidateScope == "within_import");

        // Observed (staged via the real parser + genealogy.generate_gedcom_import_duplicate_candidates):
        // name_score=1.0000 (identical normalized full name "іван петрович
        // петренко"), date_score=1.0000 (both BIRT 1900, |diff|=0),
        // place_score=1.0000 (both "Вербівка" -> "вербівка"), negative_score=0
        // (same sex, birth years equal, no sourced date conflict) =>
        // score = 1.0*0.75 + 1.0*0.15 + 1.0*0.10 - 0 = 1.0000.
        Assert.True(
            candidate.Score >= 0.90m,
            $"Expected a high-confidence (>= 0.90) score, got {candidate.Score}.");
    }

    [Fact]
    public async Task GenerateAsync_DupWithinNegativeEvidenceFixture_SourcedDateConflictSuppressesThePair()
    {
        var tree = Guid.NewGuid();
        await SeedTreeAsync(tree, "Duplicate Exit Criteria - Negative Evidence");

        var staged = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = FixturePath("dup_within_negative_evidence.ged"),
            TreeId = tree,
        });

        await _duplicateService.GenerateAsync(staged.BatchId);

        var listed = await _duplicateService.ListCandidatesAsync(staged.BatchId);

        // Observed (staged via the real parser + genealogy.generate_gedcom_import_duplicate_candidates):
        // same name/birth-year/place signal as the high-confidence fixture
        // (name_score=1.0, date_score=1.0, place_score=1.0), BUT both persons
        // have a cited DEAT event with conflicting dates (1965 vs 1990), each
        // with a real SOUR citation. That satisfies
        // staged_sourced_life_event's is_derived=false + non-blank date_raw +
        // EXISTS citation conditions for DEAT on both sides, so the lateral
        // "sourced" join finds differing date_range_keys and sets
        // has_sourced_date_conflict = 1, which adds +1.0000 to negative_score.
        // Composite score = 0.75 + 0.15 + 0.10 - 1.0 = 0.0000 (clamped to
        // [0, 1] by greatest/least, already non-negative), which is below the
        // 0.7500 p_min_score gate applied inside the SQL function itself --
        // the pair is therefore never inserted as a candidate at all, not
        // merely filtered out of a >= 0.90 high-confidence bucket.
        Assert.DoesNotContain(listed.Candidates, c => c.CandidateScope == "within_import");
        Assert.DoesNotContain(listed.Summary, s => s.CandidateScope == "within_import");
    }

    [Fact]
    public async Task ApplyAsync_DeleteMissing_DeletesExtraProductionPerson_ButOnlyWhenTrue()
    {
        var tree = Guid.NewGuid();
        var extraPersonId = Guid.NewGuid();

        await SeedTreeAsync(tree, "Apply Exit Criteria - Delete Missing");
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();

            // Hand-seeded production person the GEDCOM fixture below knows
            // nothing about -- exactly the "absent from import" case
            // delete_missing targets.
            await TestSeeding.InsertPersonAsync(
                connection,
                tree,
                extraPersonId,
                externalId: "EXTRA-1",
                primaryDisplayName: "Extra Person Not In Import",
                surnameNormalized: "extra");
        }

        var fixturePath = FixturePath("phase0_baseline.ged");

        // First apply with deleteMissing:false must leave the extra person
        // untouched.
        var firstBatch = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = fixturePath,
            TreeId = tree,
        });
        var firstResult = await _applyService.ApplyAsync(firstBatch.BatchId, deleteMissing: false, dryRun: false);
        Assert.Equal("APPLIED", firstResult.Status);
        Assert.True(await PersonExistsAsync(tree, extraPersonId));

        // Second apply (a fresh, deterministic re-stage of the same fixture)
        // with deleteMissing:true must delete the extra person, which is
        // still absent from staging.
        var secondBatch = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = fixturePath,
            TreeId = tree,
        });
        var secondResult = await _applyService.ApplyAsync(secondBatch.BatchId, deleteMissing: true, dryRun: false);
        Assert.Equal("APPLIED", secondResult.Status);
        Assert.False(await PersonExistsAsync(tree, extraPersonId));
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private async Task SeedTreeAsync(Guid tree, string name)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await TestSeeding.InsertTreeAsync(connection, tree, name);
    }

    private string FixturePath(string fileName) =>
        Path.Combine(_fixture.WorkspaceDirectory, "tools", "gedcom", "tests", "fixtures", fileName);

    private async Task AssertProductionMatchesStagedAsync(Guid tree, GedcomStagingLoadResult staged)
    {
        Assert.Equal(
            staged.RowCounts["genealogy.gedcom_import_person"],
            await CountProductionRowsAsync("genealogy.person", tree));
        Assert.Equal(
            staged.RowCounts["genealogy.gedcom_import_family"],
            await CountProductionRowsAsync("genealogy.family", tree));
        Assert.Equal(
            staged.RowCounts["genealogy.gedcom_import_family_child"],
            await CountProductionRowsAsync("genealogy.family_child", tree));
        Assert.Equal(
            staged.RowCounts["genealogy.gedcom_import_parent_of"],
            await CountProductionRowsAsync("genealogy.parent_child", tree));
        Assert.Equal(
            staged.RowCounts["genealogy.gedcom_import_event"],
            await CountProductionRowsAsync("genealogy.event", tree));
    }

    private async Task<ProductionCounts> CaptureProductionCountsAsync(Guid tree) =>
        new(
            Person: await CountProductionRowsAsync("genealogy.person", tree),
            Family: await CountProductionRowsAsync("genealogy.family", tree),
            FamilyChild: await CountProductionRowsAsync("genealogy.family_child", tree),
            ParentChild: await CountProductionRowsAsync("genealogy.parent_child", tree),
            Event: await CountProductionRowsAsync("genealogy.event", tree));

    private async Task<long> CountProductionRowsAsync(string table, Guid tree)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {table} WHERE tree_id = @tree;", connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = tree });

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> PersonExistsAsync(Guid tree, Guid personId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM genealogy.person WHERE tree_id = @tree AND person_id = @person);",
            connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = tree });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private sealed record ProductionCounts(long Person, long Family, long FamilyChild, long ParentChild, long Event);
}
