using Genealogy.Workspace.Data;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 4 exit criterion for the GEDCOM duplicate-candidate scoring port
/// (migration 0006): exercises
/// <c>genealogy.generate_gedcom_import_duplicate_candidates</c> against small
/// hand-seeded scenarios, mirroring <see cref="DatabaseLifecycleTests"/>'s
/// fixture usage (WorkspaceEnvironmentFixture + fresh DB + MigrationEngine).
///
/// Three scenarios, each in its own import batch (the tree is shared; there are
/// no production persons, so import_vs_tree is always empty and only
/// within_import pairs are produced):
///   (1) two staged persons with identical normalized full name, same birth
///       year (delta 0) and same birthplace, one disconnected => one
///       high-confidence within_import candidate.
///   (2) the same pair plus a sourced (cited, non-derived) DEAT on each side
///       with CONFLICTING dates => negative_score picks up +1.0 and the
///       composite clamps to 0, dropping the pair below min_score (not emitted).
///   (3) a matching pair where BOTH sides have parent edges (connected) => the
///       disconnected filter excludes it (no candidate).
/// </summary>
public sealed class GedcomDuplicateScoringTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    private readonly Guid _tree = Guid.NewGuid();

    public GedcomDuplicateScoringTests(WorkspaceEnvironmentFixture fixture)
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

        // A single tree satisfies the batch.tree_id foreign key; no production
        // persons are seeded, so import_vs_tree yields nothing in any scenario.
        await TestSeeding.InsertTreeAsync(connection, _tree, "Duplicate Scoring Tree");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task WithinImport_MatchingDisconnectedPair_EmitsHighConfidenceCandidate()
    {
        var batch = Guid.NewGuid();
        var personA = Guid.NewGuid();
        var personB = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await InsertBatchAsync(connection, batch);

        // Two staged persons: identical normalized name, same birth year, same
        // birthplace, same sex. person A has a parent edge; person B has none,
        // so the (parent_count = 0 OR ...) disconnected filter passes.
        await SeedPersonAsync(connection, batch, personA, nameRow: 1, sex: 'M',
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");
        await SeedPersonAsync(connection, batch, personB, nameRow: 2, sex: 'M',
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");

        await InsertBirthAsync(connection, batch, eventRow: 10, person: personA, year: 1900, place: "kyiv");
        await InsertBirthAsync(connection, batch, eventRow: 11, person: personB, year: 1900, place: "kyiv");

        await InsertParentEdgeAsync(connection, batch, parent: Guid.NewGuid(), child: personA);

        var inserted = await GenerateAsync(connection, batch);
        Assert.Equal(1, inserted);

        var rows = await ReadCandidatesAsync(connection, batch);
        var candidate = Assert.Single(rows);

        Assert.Equal("within_import", candidate.CandidateScope);
        Assert.Null(candidate.ExistingTreePersonId);
        Assert.NotNull(candidate.ImportTreePersonId2);
        // Canonical ordering: id1 < id2.
        Assert.True(candidate.ImportTreePersonId1.CompareTo(candidate.ImportTreePersonId2!.Value) < 0);

        Assert.Equal(1.0000m, candidate.NameScore);
        Assert.Equal(1.0000m, candidate.DateScore);
        Assert.Equal(1.0000m, candidate.PlaceScore);
        Assert.Equal(0.0000m, candidate.NegativeScore);
        // Faithful composite: 0.75*1 + 0.15*1 + 0.10*1 - 0 = 1.0000 (the weights
        // sum to exactly 1.0). Clamped to [0,1] => 1.0000.
        Assert.Equal(1.0000m, candidate.Score);
        Assert.Equal("review_high_confidence", candidate.RecommendedAction);
        Assert.Equal("suggested", candidate.Status);
        Assert.NotNull(candidate.EvidenceFor);
    }

    [Fact]
    public async Task WithinImport_SourcedDeathDateConflict_ClampsScoreToZero_NotEmitted()
    {
        var batch = Guid.NewGuid();
        var personA = Guid.NewGuid();
        var personB = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await InsertBatchAsync(connection, batch);

        await SeedPersonAsync(connection, batch, personA, nameRow: 1, sex: 'M',
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");
        await SeedPersonAsync(connection, batch, personB, nameRow: 2, sex: 'M',
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");

        await InsertBirthAsync(connection, batch, eventRow: 10, person: personA, year: 1900, place: "kyiv");
        await InsertBirthAsync(connection, batch, eventRow: 11, person: personB, year: 1900, place: "kyiv");

        await InsertParentEdgeAsync(connection, batch, parent: Guid.NewGuid(), child: personA);

        // Sourced (cited, non-derived) DEAT on each side with conflicting raw
        // dates and no parsed date range => date_raw_key mismatch => conflict =>
        // negative_score += 1.0 => composite clamps to 0 => below min_score.
        await InsertSourcedDeathAsync(connection, batch, eventRow: 20, citationRow: 30, person: personA, dateRaw: "1950");
        await InsertSourcedDeathAsync(connection, batch, eventRow: 21, citationRow: 31, person: personB, dateRaw: "1960");

        var inserted = await GenerateAsync(connection, batch);
        Assert.Equal(0, inserted);

        var rows = await ReadCandidatesAsync(connection, batch);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task WithinImport_ConnectedPair_ExcludedByDisconnectedFilter()
    {
        var batch = Guid.NewGuid();
        var personA = Guid.NewGuid();
        var personB = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await InsertBatchAsync(connection, batch);

        await SeedPersonAsync(connection, batch, personA, nameRow: 1, sex: 'M',
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");
        await SeedPersonAsync(connection, batch, personB, nameRow: 2, sex: 'M',
            given: "ivan", surname: "petrov", fullNormalized: "ivan petrov");

        await InsertBirthAsync(connection, batch, eventRow: 10, person: personA, year: 1900, place: "kyiv");
        await InsertBirthAsync(connection, batch, eventRow: 11, person: personB, year: 1900, place: "kyiv");

        // BOTH persons connected (each has a distinct parent) => parent_count > 0
        // for both => the (parent_count = 0 OR parent_count = 0) filter is false.
        await InsertParentEdgeAsync(connection, batch, parent: Guid.NewGuid(), child: personA);
        await InsertParentEdgeAsync(connection, batch, parent: Guid.NewGuid(), child: personB);

        var inserted = await GenerateAsync(connection, batch);
        Assert.Equal(0, inserted);

        var rows = await ReadCandidatesAsync(connection, batch);
        Assert.Empty(rows);
    }

    // --- function invocation + result readback ---------------------------------

    private static async Task<int> GenerateAsync(NpgsqlConnection connection, Guid batch)
    {
        await using var command = new NpgsqlCommand(
            "select genealogy.generate_gedcom_import_duplicate_candidates(@b, @min);", connection);
        command.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = batch });
        command.Parameters.Add(new NpgsqlParameter("min", NpgsqlDbType.Numeric) { Value = 0.7500m });
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<List<CandidateRow>> ReadCandidatesAsync(NpgsqlConnection connection, Guid batch)
    {
        await using var command = new NpgsqlCommand(
            """
            select candidate_scope, import_tree_person_id_1, import_tree_person_id_2,
                   existing_tree_person_id, score, name_score, date_score, place_score,
                   negative_score, recommended_action, status, evidence_for
            from genealogy.gedcom_import_duplicate_candidate
            where import_batch_id = @b
            order by score desc;
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = batch });

        var rows = new List<CandidateRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new CandidateRow(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    private sealed record CandidateRow(
        string CandidateScope,
        Guid ImportTreePersonId1,
        Guid? ImportTreePersonId2,
        Guid? ExistingTreePersonId,
        decimal Score,
        decimal NameScore,
        decimal DateScore,
        decimal PlaceScore,
        decimal NegativeScore,
        string RecommendedAction,
        string Status,
        string? EvidenceFor);

    // --- seed helpers ----------------------------------------------------------

    private async Task InsertBatchAsync(NpgsqlConnection connection, Guid batch)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_batch
                (import_batch_id, source_file_path, tree_id, status)
            VALUES (@b, @path, @tree, 'STAGED');
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("path", NpgsqlDbType.Text, "/tmp/dupe-test.ged"),
            ("tree", NpgsqlDbType.Uuid, _tree));
    }

    /// <summary>
    /// Inserts a staged person plus its raw name row and parsed name row (the
    /// parsed row has a composite FK to the raw row, so both are required).
    /// </summary>
    private async Task SeedPersonAsync(
        NpgsqlConnection connection, Guid batch, Guid person, int nameRow,
        char sex, string given, string surname, string fullNormalized)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_person
                (import_batch_id, tree_person_id, sex, primary_display_name, surname_normalized)
            VALUES (@b, @id, @sex, @name, @surname);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("id", NpgsqlDbType.Uuid, person),
            ("sex", NpgsqlDbType.Char, sex.ToString()),
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

    /// <summary>
    /// Inserts a non-derived DEAT event with a non-blank raw date and a single
    /// citation, so it qualifies as a "sourced life event" for conflict/match
    /// detection. No parsed date range is set, so comparison falls back to the
    /// lowercased/trimmed raw date.
    /// </summary>
    private async Task InsertSourcedDeathAsync(
        NpgsqlConnection connection, Guid batch, int eventRow, int citationRow, Guid person, string dateRaw)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event
                (import_batch_id, row_number, external_event_key, tree_person_id, event_type,
                 date_raw, is_derived)
            VALUES (@b, @row, @key, @id, 'DEAT', @date, false);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, eventRow),
            ("key", NpgsqlDbType.Varchar, $"EVT-{eventRow}"),
            ("id", NpgsqlDbType.Uuid, person),
            ("date", NpgsqlDbType.Text, dateRaw));

        await ExecuteAsync(connection,
            """
            INSERT INTO genealogy.gedcom_import_event_citation
                (import_batch_id, row_number, event_row_number, source_ref)
            VALUES (@b, @row, @evrow, @ref);
            """,
            ("b", NpgsqlDbType.Uuid, batch),
            ("row", NpgsqlDbType.Integer, citationRow),
            ("evrow", NpgsqlDbType.Integer, eventRow),
            ("ref", NpgsqlDbType.Text, $"SRC-{citationRow}"));
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
