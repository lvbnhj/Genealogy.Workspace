using System.Text.Json;
using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Staging;
using Genealogy.Workspace.McpServer.Tools;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 5 Task 5 exit criterion: exercises every <see cref="GedcomTools"/>
/// tool against a fresh, migrated database, staging the same
/// <c>phase0_baseline.ged</c> fixture used by <see cref="GedcomStagingLifecycleTests"/>.
/// The lifecycle/apply/duplicate services already have deep coverage
/// (<see cref="GedcomStagingLifecycleTests"/>, <see cref="GedcomApplyServiceTests"/>,
/// <see cref="GedcomDuplicateServiceTests"/>, <see cref="GedcomReadinessTests"/>);
/// this file only asserts each tool returns well-formed, correctly-shaped JSON.
/// </summary>
public sealed class GedcomToolsTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private GedcomTools _tools = null!;
    private string _fixtureGedcomPath = string.Empty;

    private Guid _treeId;
    private const string TreeName = "Gedcom Tools Test Tree";

    public GedcomToolsTests(WorkspaceEnvironmentFixture fixture)
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

        _tools = new GedcomTools(
            new GedcomStagingService(connectionFactory),
            new GedcomImportPreviewService(connectionFactory),
            new GedcomDuplicateService(connectionFactory),
            new GedcomReadinessService(connectionFactory),
            new GedcomApplyService(connectionFactory),
            new TreeResolver(connectionFactory));

        _fixtureGedcomPath = Path.Combine(
            _fixture.WorkspaceDirectory, "tools", "gedcom", "tests", "fixtures", "phase0_baseline.ged");
        Assert.True(File.Exists(_fixtureGedcomPath), $"Fixture not found: {_fixtureGedcomPath}");

        _treeId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await TestSeeding.InsertTreeAsync(connection, _treeId, TreeName, isDefault: true);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    // ── stage_gedcom_import ──────────────────────────────────────────────────

    [Fact]
    public async Task StageGedcomImport_ResolvesTreeByName_ReturnsBatchIdAndRowCounts()
    {
        var json = await _tools.StageGedcomImportAsync(_fixtureGedcomPath, tree: TreeName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.NotEqual(Guid.Empty, root.GetProperty("importBatchId").GetGuid());
        Assert.Equal(TreeName, root.GetProperty("tree").GetProperty("name").GetString());
        Assert.True(root.GetProperty("rowCounts").GetProperty("genealogy.gedcom_import_person").GetInt64() == 28);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task StageGedcomImport_UnknownTree_ReturnsError()
    {
        var json = await _tools.StageGedcomImportAsync(_fixtureGedcomPath, tree: "Nonexistent Tree XYZ");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
        Assert.False(root.TryGetProperty("importBatchId", out _));
    }

    // ── get_gedcom_import_preview ────────────────────────────────────────────

    [Fact]
    public async Task GetGedcomImportPreview_StagedBatch_ReturnsBatchChangesAndSamples()
    {
        var batchId = await StageFixtureAsync();

        var json = await _tools.GetGedcomImportPreviewAsync(batchId.ToString());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(batchId, root.GetProperty("batch").GetProperty("importBatchId").GetGuid());
        Assert.True(root.GetProperty("changes").GetArrayLength() > 0);
        Assert.True(root.GetProperty("samplePersonChanges").GetArrayLength() > 0);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task GetGedcomImportPreview_UnknownBatch_ReturnsError()
    {
        var json = await _tools.GetGedcomImportPreviewAsync(Guid.NewGuid().ToString());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    [Fact]
    public async Task GetGedcomImportPreview_NonGuidBatchId_ReturnsError()
    {
        var json = await _tools.GetGedcomImportPreviewAsync("not-a-guid");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    // ── get_gedcom_import_readiness_report ───────────────────────────────────

    [Fact]
    public async Task GetGedcomImportReadinessReport_StagedBatch_ReturnsGatesAndFlags()
    {
        var batchId = await StageFixtureAsync();

        var json = await _tools.GetGedcomImportReadinessReportAsync(batchId.ToString());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(batchId, root.GetProperty("importBatchId").GetGuid());
        Assert.Equal("WAITING_FOR_CONFIRMATION", root.GetProperty("status").GetString());
        Assert.Equal(4, root.GetProperty("gates").GetArrayLength());
        Assert.True(root.TryGetProperty("canApplyWithoutReview", out _));
        Assert.True(root.GetProperty("requiresExplicitConfirmation").GetBoolean());
        Assert.True(root.TryGetProperty("duplicateCount", out _));

        var gateNames = root.GetProperty("gates").EnumerateArray()
            .Select(g => g.GetProperty("gate").GetString())
            .ToList();
        Assert.Contains("high_confidence_duplicates", gateNames);
        Assert.Contains("name_parsing_issues", gateNames);
        Assert.Contains("date_warnings", gateNames);
        Assert.Contains("scope_invalid", gateNames);
    }

    // ── find_import_duplicate_candidates ─────────────────────────────────────

    [Fact]
    public async Task FindImportDuplicateCandidates_StagedBatch_ReturnsSummaryAndCandidatesShape()
    {
        var batchId = await StageFixtureAsync();

        var json = await _tools.FindImportDuplicateCandidatesAsync(batchId.ToString());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(batchId, root.GetProperty("importBatchId").GetGuid());
        Assert.Equal(0.75m, root.GetProperty("minScore").GetDecimal());
        Assert.True(root.GetProperty("result").TryGetProperty("summary", out _));
        Assert.True(root.GetProperty("result").TryGetProperty("candidates", out _));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("note").GetString()));
    }

    // ── list_pending_gedcom_imports ───────────────────────────────────────────

    [Fact]
    public async Task ListPendingGedcomImports_ResolvedTree_SurfacesStagedBatch()
    {
        var batchId = await StageFixtureAsync();

        var json = await _tools.ListPendingGedcomImportsAsync(tree: TreeName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(TreeName, root.GetProperty("tree").GetString());
        Assert.True(root.GetProperty("includePreviewed").GetBoolean());

        var batchIds = root.GetProperty("pendingImports").EnumerateArray()
            .Select(p => p.GetProperty("importBatchId").GetGuid())
            .ToList();
        Assert.Contains(batchId, batchIds);
    }

    [Fact]
    public async Task ListPendingGedcomImports_NoTree_ReturnsAllTreesPending()
    {
        await StageFixtureAsync();

        var json = await _tools.ListPendingGedcomImportsAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.GetProperty("pendingImports").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ListPendingGedcomImports_UnknownTree_ReturnsError()
    {
        var json = await _tools.ListPendingGedcomImportsAsync(tree: "Nonexistent Tree XYZ");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    // ── apply_gedcom_import (dry run) ─────────────────────────────────────────

    [Fact]
    public async Task ApplyGedcomImport_DryRunDefault_ReturnsPreviewChangesAndDoesNotApply()
    {
        var batchId = await StageFixtureAsync();

        var json = await _tools.ApplyGedcomImportAsync(batchId.ToString());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        var applyStatus = root.GetProperty("applyStatus");
        Assert.Equal(batchId, applyStatus.GetProperty("importBatchId").GetGuid());
        Assert.True(applyStatus.GetProperty("dryRun").GetBoolean());
        Assert.True(root.GetProperty("changes").GetArrayLength() > 0);
        Assert.Contains("Dry run", root.GetProperty("note").GetString());

        // Dry run must not have flipped the batch status away from STAGED/PREVIEWED.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT status FROM genealogy.gedcom_import_batch WHERE import_batch_id = @id;", connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = batchId });
        var status = (string)(await command.ExecuteScalarAsync())!;
        Assert.NotEqual("APPLIED", status);
    }

    // ── cancel_gedcom_import ───────────────────────────────────────────────────

    [Fact]
    public async Task CancelGedcomImport_StagedBatch_ReturnsCancelledResult()
    {
        var batchId = await StageFixtureAsync();

        var json = await _tools.CancelGedcomImportAsync(batchId.ToString(), reason: "test cancel via tool");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        var result = root.GetProperty("result");
        Assert.Equal(batchId, result.GetProperty("importBatchId").GetGuid());
        Assert.Equal("CANCELLED", result.GetProperty("status").GetString());
        Assert.Contains("test cancel via tool", result.GetProperty("notes").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("note").GetString()));

        // Cancelled batches drop out of the pending list.
        var pendingJson = await _tools.ListPendingGedcomImportsAsync(tree: TreeName);
        using var pendingDoc = JsonDocument.Parse(pendingJson);
        var pendingIds = pendingDoc.RootElement.GetProperty("pendingImports").EnumerateArray()
            .Select(p => p.GetProperty("importBatchId").GetGuid())
            .ToList();
        Assert.DoesNotContain(batchId, pendingIds);
    }

    [Fact]
    public async Task CancelGedcomImport_UnknownBatch_ReturnsError()
    {
        var json = await _tools.CancelGedcomImportAsync(Guid.NewGuid().ToString());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    // ── get_duplicate_candidate_detail / reject (not-found paths) ────────────

    [Fact]
    public async Task GetDuplicateCandidateDetail_UnknownId_ReturnsError()
    {
        var json = await _tools.GetDuplicateCandidateDetailAsync(999_999_999L);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    [Fact]
    public async Task RejectTreePersonMergeCandidate_UnknownId_ReturnsError()
    {
        var json = await _tools.RejectTreePersonMergeCandidateAsync(999_999_999L);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    // ── seeding helper ────────────────────────────────────────────────────────

    private async Task<Guid> StageFixtureAsync()
    {
        var json = await _tools.StageGedcomImportAsync(_fixtureGedcomPath, tree: TreeName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _), json);
        return root.GetProperty("importBatchId").GetGuid();
    }
}
