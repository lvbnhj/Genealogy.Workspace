using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 4, Task B exit criterion for the GEDCOM duplicate-candidate SERVICE
/// (<see cref="GedcomDuplicateService"/>): generate/list/detail/reject against
/// a hand-seeded within_import scenario, mirroring
/// <see cref="GedcomDuplicateScoringTests"/>'s and
/// <see cref="GedcomImportPreviewTests"/>'s fixture usage (WorkspaceEnvironmentFixture
/// + fresh DB + MigrationEngine).
///
/// Scenario (one tree, one batch, two matching staged persons):
///   - "connected" person: identical normalized full name + birth year/place as
///     "disconnected"; has a parent, a child, a spouse (via a staged family)
///     and a sourced (cited) BIRT event.
///   - "disconnected" person: same name/birth year/place, no family edges at all.
/// The disconnected person's zero parent-count satisfies the scoring
/// function's "(n1.parent_count = 0 or n2.parent_count = 0)" filter, so
/// exactly one high-confidence within_import candidate is produced.
/// </summary>
public sealed class GedcomDuplicateServiceTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private GedcomDuplicateService _service = null!;

    private readonly Guid _tree = Guid.NewGuid();
    private readonly Guid _batch = Guid.NewGuid();
    private readonly Guid _personConnected = Guid.NewGuid();
    private readonly Guid _personDisconnected = Guid.NewGuid();
    private readonly Guid _parentOfConnected = Guid.NewGuid();
    private readonly Guid _childOfConnected = Guid.NewGuid();
    private readonly Guid _spouseOfConnected = Guid.NewGuid();

    public GedcomDuplicateServiceTests(WorkspaceEnvironmentFixture fixture)
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

        _service = new GedcomDuplicateService(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _tree, "Duplicate Service Test Tree");
        await InsertBatchAsync(connection, _batch, _tree);

        // Two staged persons with identical normalized full name, birth year
        // and birthplace => a within_import match candidate.
        await SeedMatchingPersonAsync(connection, _batch, _personConnected, nameRow: 1,
            displayName: "Ivan Petrov");
        await SeedMatchingPersonAsync(connection, _batch, _personDisconnected, nameRow: 2,
            displayName: "Ivan Petrov Disconnected");

        await InsertBirthAsync(connection, _batch, eventRow: 10, person: _personConnected, year: 1900, place: "kyiv");
        await InsertBirthAsync(connection, _batch, eventRow: 11, person: _personDisconnected, year: 1900, place: "kyiv");

        // Sourced citation on the connected side's BIRT event only.
        await InsertCitationAsync(connection, _batch, citationRow: 100, eventRow: 10,
            sourceTitle: "Parish Register", sourceRef: "PR-1");

        // Family evidence for the connected side only: a parent, a child, and
        // a spouse via a staged family. The disconnected side has none of
        // this, which is exactly what makes it "disconnected" for scoring.
        await SeedPersonShellAsync(connection, _batch, _parentOfConnected, "Petro Ivanovych");
        await InsertParentEdgeAsync(connection, _batch, parent: _parentOfConnected, child: _personConnected);

        await SeedPersonShellAsync(connection, _batch, _childOfConnected, "Olena Ivanivna");
        await InsertParentEdgeAsync(connection, _batch, parent: _personConnected, child: _childOfConnected);

        await SeedPersonShellAsync(connection, _batch, _spouseOfConnected, "Maria Kovalenko");
        await InsertFamilyAsync(connection, _batch, familyId: Guid.NewGuid(),
            spouse1: _personConnected, spouse2: _spouseOfConnected, marriageYear: 1925);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task FullLifecycle_Generate_List_Detail_Reject_SuppressesOnRegenerate()
    {
        // --- Generate ---
        var inserted = await _service.GenerateAsync(_batch);
        Assert.Equal(1, inserted);

        // --- List: summary ---
        var listResult = await _service.ListCandidatesAsync(_batch);

        var summary = Assert.Single(listResult.Summary);
        Assert.Equal("within_import", summary.CandidateScope);
        Assert.Equal(1, summary.CandidateCount);
        Assert.Equal(1, summary.HighConfidenceCount);
        Assert.Equal(0, summary.ProbableCount);
        Assert.Equal(1.0000m, summary.MaxScore);

        // --- List: candidate row ---
        var candidate = Assert.Single(listResult.Candidates);
        Assert.Equal("within_import", candidate.CandidateScope);
        Assert.Null(candidate.ExistingTreePersonId);
        Assert.Null(candidate.ExistingPersonName);
        Assert.NotNull(candidate.ImportTreePersonId2);
        Assert.Equal("suggested", candidate.Status);
        Assert.Equal("review_high_confidence", candidate.RecommendedAction);
        Assert.Equal(1.0000m, candidate.Score);

        var names = new[] { candidate.ImportPerson1Name, candidate.ImportPerson2Name };
        Assert.Contains("Ivan Petrov", names);
        Assert.Contains("Ivan Petrov Disconnected", names);

        // Figure out which side of the candidate is the "connected" person,
        // since least()/greatest() ordering depends on the raw GUID values.
        var connectedSide = candidate.ImportTreePersonId1 == _personConnected ? "import1" : "import2";
        Assert.Equal(_personConnected,
            connectedSide == "import1" ? candidate.ImportTreePersonId1 : candidate.ImportTreePersonId2);

        // --- Detail ---
        var detail = await _service.GetCandidateDetailAsync(candidate.DuplicateCandidateId);
        Assert.NotNull(detail);
        Assert.Equal(candidate.DuplicateCandidateId, detail!.DuplicateCandidateId);
        Assert.Equal(_batch, detail.ImportBatchId);
        Assert.Equal("within_import", detail.CandidateScope);
        Assert.Equal("suggested", detail.Status);
        Assert.Equal(1.0000m, detail.Score);
        Assert.Null(detail.ExistingTreePersonId);

        // Event with a citation, on the connected side.
        var birtEvent = Assert.Single(detail.Events, e => e.Side == connectedSide && e.EventType == "BIRT");
        Assert.True(birtEvent.HasSourceCitation);
        Assert.NotNull(birtEvent.CitationSummary);
        Assert.Contains("Parish Register", birtEvent.CitationSummary);
        Assert.Contains("PR-1", birtEvent.CitationSummary);

        // The disconnected side's BIRT event has no citation.
        var otherSide = connectedSide == "import1" ? "import2" : "import1";
        var otherBirtEvent = Assert.Single(detail.Events, e => e.Side == otherSide && e.EventType == "BIRT");
        Assert.False(otherBirtEvent.HasSourceCitation);
        Assert.Null(otherBirtEvent.CitationSummary);

        // Expanded family evidence, populated only for the connected side.
        var parent = Assert.Single(detail.Parents, p => p.Side == connectedSide);
        Assert.Equal(_parentOfConnected, parent.PersonId);
        Assert.Equal("Petro Ivanovych", parent.DisplayName);
        Assert.DoesNotContain(detail.Parents, p => p.Side == otherSide);

        var child = Assert.Single(detail.Children, c => c.Side == connectedSide);
        Assert.Equal(_childOfConnected, child.PersonId);
        Assert.Equal("Olena Ivanivna", child.DisplayName);
        Assert.DoesNotContain(detail.Children, c => c.Side == otherSide);

        var spouse = Assert.Single(detail.Spouses, s => s.Side == connectedSide);
        Assert.Equal(_spouseOfConnected, spouse.PersonId);
        Assert.Equal("Maria Kovalenko", spouse.DisplayName);
        Assert.Equal((short)1925, spouse.MarriageYear);
        Assert.DoesNotContain(detail.Spouses, s => s.Side == otherSide);

        // --- Reject ---
        var rejected = await _service.RejectAsync(candidate.DuplicateCandidateId);
        Assert.Equal(candidate.DuplicateCandidateId, rejected.DuplicateCandidateId);
        Assert.Equal("rejected", rejected.Status);

        // Listing again finds nothing: the row is no longer 'suggested'.
        var listAfterReject = await _service.ListCandidatesAsync(_batch);
        Assert.Empty(listAfterReject.Summary);
        Assert.Empty(listAfterReject.Candidates);

        // --- Rejected-suppression on regenerate ---
        var insertedAgain = await _service.GenerateAsync(_batch);
        Assert.Equal(0, insertedAgain);

        var listAfterRegenerate = await _service.ListCandidatesAsync(_batch);
        Assert.Empty(listAfterRegenerate.Candidates);
    }

    [Fact]
    public async Task ListCandidatesAsync_UnknownBatch_Throws()
    {
        await Assert.ThrowsAsync<GedcomImportBatchNotFoundException>(
            () => _service.ListCandidatesAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetCandidateDetailAsync_UnknownId_ReturnsNull()
    {
        var detail = await _service.GetCandidateDetailAsync(long.MaxValue);
        Assert.Null(detail);
    }

    [Fact]
    public async Task RejectAsync_UnknownId_Throws()
    {
        await Assert.ThrowsAsync<GedcomDuplicateCandidateNotFoundException>(
            () => _service.RejectAsync(long.MaxValue));
    }

    // --- seed helpers (kept local to avoid touching the shared TestSeeding
    //     helper another task may edit in parallel) ---

    private async Task InsertBatchAsync(NpgsqlConnection connection, Guid batch, Guid tree)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_batch
                (import_batch_id, source_file_path, tree_id, status)
            VALUES (@b, @path, @tree, 'STAGED');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("path", NpgsqlDbType.Text, "/tmp/dupe-service-test.ged"),
            ("tree", NpgsqlDbType.Uuid, tree));
    }

    /// <summary>
    /// Inserts a staged person plus its raw name row and parsed name row (the
    /// parsed row has a composite FK to the raw row, so both are required)
    /// with a fixed given/surname/normalized-name/sex/birth so two calls with
    /// different <paramref name="displayName"/>s produce a scoring match.
    /// </summary>
    private async Task SeedMatchingPersonAsync(
        NpgsqlConnection connection, Guid batch, Guid person, int nameRow, string displayName)
    {
        const string given = "ivan";
        const string surname = "petrov";
        const string fullNormalized = "ivan petrov";

        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person
                (import_batch_id, tree_person_id, sex, primary_display_name, surname_normalized)
            VALUES (@b, @id, 'M', @name, @surname);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("id", NpgsqlDbType.Uuid, person),
            ("name", NpgsqlDbType.Text, displayName),
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
            ("full", NpgsqlDbType.Text, displayName),
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
            ("full", NpgsqlDbType.Text, displayName),
            ("given", NpgsqlDbType.Text, given),
            ("surname", NpgsqlDbType.Text, surname),
            ("norm", NpgsqlDbType.Text, fullNormalized));
    }

    /// <summary>
    /// Inserts a bare staged person row (no parsed name) purely so a
    /// parent/child/spouse edge has a <c>gedcom_import_person</c> row to join
    /// against for its display name. Never a matching candidate on its own.
    /// </summary>
    private async Task SeedPersonShellAsync(NpgsqlConnection connection, Guid batch, Guid person, string displayName)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person
                (import_batch_id, tree_person_id, sex, primary_display_name)
            VALUES (@b, @id, 'F', @name);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("id", NpgsqlDbType.Uuid, person),
            ("name", NpgsqlDbType.Text, displayName));
    }

    private async Task InsertBirthAsync(
        NpgsqlConnection connection, Guid batch, int eventRow, Guid person, int year, string place)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event
                (import_batch_id, row_number, external_event_key, tree_person_id, event_type,
                 date_raw, year_from, place_raw, place_normalized, is_derived)
            VALUES (@b, @row, @key, @id, 'BIRT', @dateraw, @year, @place, @place, false);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, eventRow),
            ("key", NpgsqlDbType.Varchar, $"EVT-{eventRow}"),
            ("id", NpgsqlDbType.Uuid, person),
            ("dateraw", NpgsqlDbType.Text, year.ToString()),
            ("year", NpgsqlDbType.Smallint, (short)year),
            ("place", NpgsqlDbType.Text, place));
    }

    private async Task InsertCitationAsync(
        NpgsqlConnection connection, Guid batch, int citationRow, int eventRow, string sourceTitle, string sourceRef)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event_citation
                (import_batch_id, row_number, event_row_number, source_title, source_ref)
            VALUES (@b, @row, @evrow, @title, @ref);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, citationRow),
            ("evrow", NpgsqlDbType.Integer, eventRow),
            ("title", NpgsqlDbType.Text, sourceTitle),
            ("ref", NpgsqlDbType.Text, sourceRef));
    }

    private async Task InsertParentEdgeAsync(NpgsqlConnection connection, Guid batch, Guid parent, Guid child)
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

    private async Task InsertFamilyAsync(
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
