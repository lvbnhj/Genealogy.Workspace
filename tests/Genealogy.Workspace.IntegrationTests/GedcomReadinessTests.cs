using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 4 exit criterion for the GEDCOM import readiness report (migration
/// 0007 + <see cref="GedcomReadinessService"/>): mirrors
/// <see cref="DatabaseLifecycleTests"/>'s fixture usage (WorkspaceEnvironmentFixture
/// + fresh DB + MigrationEngine) and, like <see cref="GedcomDuplicateScoringTests"/>
/// and <see cref="GedcomImportPreviewTests"/>, seeds raw staging rows directly
/// rather than going through the artifact loader.
///
/// This report is ADVISORY ONLY: the gate labels (blocker/warning/pass) do not
/// enforce anything, and there is no confirmation token (per the plan's "no
/// gate" decision). These tests assert the labels/counts and the status
/// transition, not any blocking behaviour.
///
/// One batch seeds all four gate conditions at once:
///   (a) two staged persons with an identical normalized name, same birth
///       year/place, both disconnected (no staged parents) => one
///       within_import 'suggested' candidate at score 1.0 (>= 0.9000) =>
///       high_confidence_duplicates = blocker, canApplyWithoutReview = false.
///   (b) a third staged person whose parsed name has parser_status = 'AMBIGUOUS'
///       (<> 'OK') => name_parsing_issues = warning.
///   (c) a date_warning row (kind APPROXIMATE) on a staged event => date_warnings = warning.
///   (d) scope_invalid_count is never set on the batch (NULL) => scope_invalid = pass.
/// The batch starts life as STAGED and must end as WAITING_FOR_CONFIRMATION.
/// </summary>
public sealed class GedcomReadinessTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private GedcomReadinessService _service = null!;

    private readonly Guid _tree = Guid.NewGuid();

    public GedcomReadinessTests(WorkspaceEnvironmentFixture fixture)
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

        _service = new GedcomReadinessService(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // A single tree satisfies the batch.tree_id foreign key; no production
        // persons are seeded (this report only reads staging + duplicate
        // candidate tables).
        await TestSeeding.InsertTreeAsync(connection, _tree, "Readiness Test Tree");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task GetReadinessAsync_UnknownBatch_Throws()
    {
        await Assert.ThrowsAsync<GedcomImportBatchNotFoundException>(
            () => _service.GetReadinessAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetReadinessAsync_ComputesAllFourGates_AndTransitionsStatus()
    {
        var batch = Guid.NewGuid();
        var personA = Guid.NewGuid();
        var personB = Guid.NewGuid();
        var personC = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await InsertBatchAsync(connection, batch);

        // (a) high_confidence_duplicates: identical normalized name, same
        // birth year + place, both disconnected => within_import candidate at
        // score 1.0 once genealogy.generate_gedcom_import_duplicate_candidates
        // runs (the service calls it before reading gates).
        await SeedMatchingPersonAsync(connection, batch, personA, nameRow: 1,
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");
        await SeedMatchingPersonAsync(connection, batch, personB, nameRow: 2,
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");
        await InsertBirthAsync(connection, batch, eventRow: 10, person: personA, year: 1900, place: "kyiv");
        await InsertBirthAsync(connection, batch, eventRow: 11, person: personB, year: 1900, place: "kyiv");

        // (b) name_parsing_issues: a third person whose parsed name failed to
        // resolve unambiguously.
        await SeedAmbiguousNamedPersonAsync(connection, batch, personC, nameRow: 3);

        // (c) date_warnings: an approximate-date warning on its own event.
        await InsertBirthAsync(connection, batch, eventRow: 20, person: personC, year: 1905, place: "lviv");
        await InsertDateWarningAsync(connection, batch, eventRow: 20, warningKind: "APPROXIMATE");

        // (d) scope_invalid: left NULL on the batch row (never set) => 0 => pass.

        var report = await _service.GetReadinessAsync(batch);

        Assert.Equal(batch, report.ImportBatchId);
        Assert.Equal("WAITING_FOR_CONFIRMATION", report.Status);
        Assert.True(report.RequiresExplicitConfirmation);
        Assert.False(report.CanApplyWithoutReview);
        Assert.Equal(1, report.DuplicateCount);

        Assert.Equal(4, report.Gates.Count);

        var highConfidenceDuplicates = Single(report.Gates, "high_confidence_duplicates");
        Assert.Equal("blocker", highConfidenceDuplicates.Severity);
        Assert.Equal(1, highConfidenceDuplicates.Count);

        var nameParsingIssues = Single(report.Gates, "name_parsing_issues");
        Assert.Equal("warning", nameParsingIssues.Severity);
        Assert.Equal(1, nameParsingIssues.Count);

        var dateWarnings = Single(report.Gates, "date_warnings");
        Assert.Equal("warning", dateWarnings.Severity);
        Assert.Equal(1, dateWarnings.Count);

        var scopeInvalid = Single(report.Gates, "scope_invalid");
        Assert.Equal("pass", scopeInvalid.Severity);
        Assert.Equal(0, scopeInvalid.Count);

        // Batch status persisted the transition.
        await using var verify = new NpgsqlConnection(_connectionString);
        await verify.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, previewed_at FROM genealogy.gedcom_import_batch WHERE import_batch_id = @b;", verify);
        command.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = batch });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("WAITING_FOR_CONFIRMATION", reader.GetString(0));
        Assert.False(reader.IsDBNull(1));
    }

    [Fact]
    public async Task GetReadinessAsync_NoIssues_AllGatesPass_AndCanApplyWithoutReview()
    {
        var batch = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Nothing seeded beyond the batch itself: no staged persons, no
        // duplicate candidates, no name-parse issues, no date warnings, and
        // scope_invalid_count stays NULL.
        await InsertBatchAsync(connection, batch);

        var report = await _service.GetReadinessAsync(batch);

        Assert.Equal("WAITING_FOR_CONFIRMATION", report.Status);
        Assert.True(report.CanApplyWithoutReview);
        Assert.True(report.RequiresExplicitConfirmation);
        Assert.Equal(0, report.DuplicateCount);

        Assert.Equal(4, report.Gates.Count);
        Assert.All(report.Gates, g => Assert.Equal("pass", g.Severity));
        Assert.All(report.Gates, g => Assert.Equal(0, g.Count));
    }

    private static GedcomReadinessGate Single(IReadOnlyList<GedcomReadinessGate> gates, string gate) =>
        Assert.Single(gates, g => g.Gate == gate);

    // --- seed helpers -----------------------------------------------------

    private async Task InsertBatchAsync(NpgsqlConnection connection, Guid batch)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_batch
                (import_batch_id, source_file_path, tree_id, status)
            VALUES (@b, @path, @tree, 'STAGED');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("path", NpgsqlDbType.Text, "/tmp/readiness-test.ged"),
            ("tree", NpgsqlDbType.Uuid, _tree));
    }

    /// <summary>
    /// Inserts a staged person plus its raw name row and an 'OK' parsed name
    /// row eligible for duplicate scoring (mirrors
    /// GedcomDuplicateScoringTests.SeedPersonAsync).
    /// </summary>
    private async Task SeedMatchingPersonAsync(
        NpgsqlConnection connection, Guid batch, Guid person, int nameRow,
        string given, string surname, string fullNormalized)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person
                (import_batch_id, tree_person_id, sex, primary_display_name, surname_normalized)
            VALUES (@b, @id, 'M', @name, @surname);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("id", NpgsqlDbType.Uuid, person),
            ("name", NpgsqlDbType.Text, $"{given} {surname}"),
            ("surname", NpgsqlDbType.Text, surname));

        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person_name
                (import_batch_id, row_number, tree_person_id, script_code, name_type,
                 given, surname, full_name, full_name_normalized, is_primary)
            VALUES (@b, @row, @id, 'LATN', 'BIRTH', @given, @surname, @full, @norm, true);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, nameRow),
            ("id", NpgsqlDbType.Uuid, person),
            ("given", NpgsqlDbType.Text, given),
            ("surname", NpgsqlDbType.Text, surname),
            ("full", NpgsqlDbType.Text, $"{given} {surname}"),
            ("norm", NpgsqlDbType.Text, fullNormalized));

        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person_name_parsed
                (import_batch_id, row_number, source_name_row_number, tree_person_id, raw_name,
                 given_name_normalized, surname_normalized, full_name_normalized,
                 normalization_confidence, parser_status)
            VALUES (@b, @row, @row, @id, @full, @given, @surname, @norm, 0.9500, 'OK');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, nameRow),
            ("id", NpgsqlDbType.Uuid, person),
            ("full", NpgsqlDbType.Text, $"{given} {surname}"),
            ("given", NpgsqlDbType.Text, given),
            ("surname", NpgsqlDbType.Text, surname),
            ("norm", NpgsqlDbType.Text, fullNormalized));
    }

    /// <summary>
    /// Inserts a staged person whose parsed name is deliberately AMBIGUOUS
    /// (parser_status &lt;&gt; 'OK'), tripping the name_parsing_issues gate.
    /// Not eligible for duplicate scoring (staged_name in migration 0006
    /// requires parser_status = 'OK'), so it cannot interfere with (a).
    /// </summary>
    private async Task SeedAmbiguousNamedPersonAsync(
        NpgsqlConnection connection, Guid batch, Guid person, int nameRow)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person
                (import_batch_id, tree_person_id, sex, primary_display_name, surname_normalized)
            VALUES (@b, @id, 'F', 'Unclear Name', 'unclear');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("id", NpgsqlDbType.Uuid, person));

        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person_name
                (import_batch_id, row_number, tree_person_id, script_code, name_type,
                 full_name, full_name_normalized, is_primary)
            VALUES (@b, @row, @id, 'LATN', 'BIRTH', 'Unclear Name', 'unclear name', true);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, nameRow),
            ("id", NpgsqlDbType.Uuid, person));

        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person_name_parsed
                (import_batch_id, row_number, source_name_row_number, tree_person_id, raw_name,
                 full_name_normalized, normalization_confidence, parser_status)
            VALUES (@b, @row, @row, @id, 'Unclear Name', 'unclear name', 0.5000, 'AMBIGUOUS');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, nameRow),
            ("id", NpgsqlDbType.Uuid, person));
    }

    private async Task InsertBirthAsync(
        NpgsqlConnection connection, Guid batch, int eventRow, Guid person, int year, string place)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event
                (import_batch_id, row_number, external_event_key, tree_person_id, event_type,
                 year_from, place_normalized, is_derived)
            VALUES (@b, @row, @key, @id, 'BIRT', @year, @place, false);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, eventRow),
            ("key", NpgsqlDbType.Varchar, $"EVT-{eventRow}"),
            ("id", NpgsqlDbType.Uuid, person),
            ("year", NpgsqlDbType.Smallint, (short)year),
            ("place", NpgsqlDbType.Text, place));
    }

    private async Task InsertDateWarningAsync(
        NpgsqlConnection connection, Guid batch, int eventRow, string warningKind)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_date_warning
                (import_batch_id, event_row_number, event_type, warning_kind, warning_message)
            VALUES (@b, @row, 'BIRT', @kind, 'approximate date');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, eventRow),
            ("kind", NpgsqlDbType.Varchar, warningKind));
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
