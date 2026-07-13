using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 3 exit criteria exercised end-to-end against a real PostgreSQL
/// database: staging a GEDCOM through <see cref="GedcomStagingService.StageAsync"/>
/// produces stable rows for the fixture used by the Python parser's own
/// regression tests (<c>tools/gedcom/tests/test_gedcom_phase0_baseline.py</c>,
/// <c>phase0_baseline.ged</c>: 28 persons, 8 families, 83 events, 6 places, 22
/// date warnings, 0 name-parse issues); pending-list surfaces it and stops
/// surfacing it once cancelled; and cancel never touches staging rows.
/// </summary>
public sealed class GedcomStagingLifecycleTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private GedcomStagingService _service = null!;
    private string _fixtureGedcomPath = string.Empty;

    private Guid _treeId;

    public GedcomStagingLifecycleTests(WorkspaceEnvironmentFixture fixture)
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

        _service = new GedcomStagingService(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        _fixtureGedcomPath = Path.Combine(
            _fixture.WorkspaceDirectory, "tools", "gedcom", "tests", "fixtures", "phase0_baseline.ged");
        Assert.True(File.Exists(_fixtureGedcomPath), $"Fixture not found: {_fixtureGedcomPath}");

        _treeId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await TestSeeding.InsertTreeAsync(connection, _treeId, "Gedcom Staging Lifecycle Tree");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task StageAsync_ParsesFixtureAndLoadsStaging_WithExpectedCounts()
    {
        var request = new GedcomStageRequest
        {
            GedcomFilePath = _fixtureGedcomPath,
            TreeId = _treeId,
        };

        var result = await _service.StageAsync(request);

        Assert.NotEqual(Guid.Empty, result.BatchId);
        Assert.Equal(28, result.RowCounts["genealogy.gedcom_import_person"]);
        Assert.Equal(8, result.RowCounts["genealogy.gedcom_import_family"]);
        Assert.Equal(83, result.RowCounts["genealogy.gedcom_import_event"]);
        Assert.Equal(6, result.RowCounts["genealogy.gedcom_import_place"]);
        Assert.Equal(22, result.RowCounts["genealogy.gedcom_import_date_warning"]);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT status, tree_id, person_count, family_count, event_count, place_count
            FROM genealogy.gedcom_import_batch
            WHERE import_batch_id = @import_batch_id;
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = result.BatchId });

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("STAGED", reader.GetString(0));
        Assert.Equal(_treeId, reader.GetGuid(1));
        Assert.Equal(28, reader.GetInt32(2));
        Assert.Equal(8, reader.GetInt32(3));
        Assert.Equal(83, reader.GetInt32(4));
        Assert.Equal(6, reader.GetInt32(5));
    }

    [Fact]
    public async Task ListPendingAsync_SurfacesStagedBatch_AndExcludesItAfterCancel()
    {
        var request = new GedcomStageRequest
        {
            GedcomFilePath = _fixtureGedcomPath,
            TreeId = _treeId,
        };
        var staged = await _service.StageAsync(request);

        var pendingBeforeCancel = await _service.ListPendingAsync(treeId: _treeId);
        var pendingBatch = Assert.Single(pendingBeforeCancel, p => p.ImportBatchId == staged.BatchId);
        Assert.Equal("STAGED", pendingBatch.Status);
        Assert.Equal("Gedcom Staging Lifecycle Tree", pendingBatch.TreeName);
        Assert.Equal(28, pendingBatch.PersonCount);
        Assert.Equal(0, pendingBatch.NameIssueCount);
        Assert.Equal(22, pendingBatch.DateWarningCount);

        await _service.CancelAsync(staged.BatchId);

        var pendingAfterCancel = await _service.ListPendingAsync(treeId: _treeId);
        Assert.DoesNotContain(pendingAfterCancel, p => p.ImportBatchId == staged.BatchId);
    }

    [Fact]
    public async Task CancelAsync_FlipsStatus_IsIdempotent_ThrowsOnMissingId_AndLeavesStagingRowsIntact()
    {
        var request = new GedcomStageRequest
        {
            GedcomFilePath = _fixtureGedcomPath,
            TreeId = _treeId,
        };
        var staged = await _service.StageAsync(request);

        var stagingPersonCountBeforeCancel = await CountStagingPersonsAsync(staged.BatchId);
        Assert.Equal(28, stagingPersonCountBeforeCancel);

        var firstCancel = await _service.CancelAsync(staged.BatchId, reason: "test cancellation");
        Assert.Equal("CANCELLED", firstCancel.Status);
        Assert.NotNull(firstCancel.CancelledAt);
        Assert.Contains("Cancelled: test cancellation", firstCancel.Notes);

        // Idempotent: re-cancelling does not throw, does not change status, and
        // does not append the reason a second time (the WHERE status <>
        // 'CANCELLED' guard makes the second UPDATE affect zero rows).
        var secondCancel = await _service.CancelAsync(staged.BatchId, reason: "second reason");
        Assert.Equal("CANCELLED", secondCancel.Status);
        Assert.Equal(firstCancel.CancelledAt, secondCancel.CancelledAt);
        Assert.Equal(firstCancel.Notes, secondCancel.Notes);
        Assert.DoesNotContain("second reason", secondCancel.Notes ?? string.Empty);

        await Assert.ThrowsAsync<GedcomImportBatchNotFoundException>(
            () => _service.CancelAsync(Guid.NewGuid()));

        // Cancel never deletes staging rows.
        var stagingPersonCountAfterCancel = await CountStagingPersonsAsync(staged.BatchId);
        Assert.Equal(stagingPersonCountBeforeCancel, stagingPersonCountAfterCancel);
    }

    [Fact]
    public async Task CancelAsync_ThrowsAlreadyApplied_WhenBatchStatusIsApplied()
    {
        var request = new GedcomStageRequest
        {
            GedcomFilePath = _fixtureGedcomPath,
            TreeId = _treeId,
        };
        var staged = await _service.StageAsync(request);

        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE genealogy.gedcom_import_batch SET status = 'APPLIED', applied_at = now() WHERE import_batch_id = @import_batch_id;",
                connection);
            command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = staged.BatchId });
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<GedcomImportBatchAlreadyAppliedException>(
            () => _service.CancelAsync(staged.BatchId));
    }

    private async Task<long> CountStagingPersonsAsync(Guid importBatchId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM genealogy.gedcom_import_person WHERE import_batch_id = @import_batch_id;",
            connection);
        command.Parameters.Add(new NpgsqlParameter("import_batch_id", NpgsqlDbType.Uuid) { Value = importBatchId });

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
