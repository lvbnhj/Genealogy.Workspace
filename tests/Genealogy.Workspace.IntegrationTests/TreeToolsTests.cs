using System.Text.Json;
using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Traversal;
using Genealogy.Workspace.McpServer.Tools;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 5 Task 4 exit criterion: exercises every <see cref="TreeTools"/>
/// tool added on top of the Task 1-3 repositories/resolvers, against a fresh,
/// migrated database seeded with a small deterministic tree. Mirrors the
/// <see cref="TreeTraversalTests"/>/<see cref="FamilyContextTests"/> fixture
/// pattern: each test class instance gets its own database created in
/// <see cref="InitializeAsync"/> and force-dropped in <see cref="DisposeAsync"/>.
///
/// The seeded tree (tree "Tools Tree", marked default):
///
///   gen 0:           grandpa (M, b.1900) == grandma (F, b.1905, m.1925)
///                                 |
///   gen 1:      father (M, b.1930) -- aunt (F, b.1928)   (father's sibling)
///                     |
///                (m. mother, F, b.1932, m.1953 -- mother has no parents)
///                     |
///   gen 2:            child (M, b.1955, BIRT event with DateRaw/PlaceRaw)
///
///   plus two isolated persons both named "Same Name" (identical
///   full_name_normalized) to exercise the PersonResolver multi-match path.
/// </summary>
public sealed class TreeToolsTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private TreeTools _tools = null!;

    private Guid _tree;

    private Guid _grandpa;
    private Guid _grandma;
    private Guid _father;
    private Guid _mother;
    private Guid _aunt;
    private Guid _child;

    private Guid _familyGrandparents;
    private Guid _familyParents;

    private Guid _dup1;
    private Guid _dup2;
    private Guid _isolated;

    public TreeToolsTests(WorkspaceEnvironmentFixture fixture)
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

        _tools = new TreeTools(
            new TreeRepository(connectionFactory),
            new PersonRepository(connectionFactory),
            new PersonSearchRepository(connectionFactory),
            new RichFamilyContextRepository(connectionFactory),
            new PersonEventsRepository(connectionFactory),
            new TreeTraversalRepository(connectionFactory),
            new TreeResolver(connectionFactory),
            new PersonResolver(connectionFactory));

        _tree = Guid.NewGuid();
        _grandpa = Guid.NewGuid();
        _grandma = Guid.NewGuid();
        _father = Guid.NewGuid();
        _mother = Guid.NewGuid();
        _aunt = Guid.NewGuid();
        _child = Guid.NewGuid();
        _familyGrandparents = Guid.NewGuid();
        _familyParents = Guid.NewGuid();
        _dup1 = Guid.NewGuid();
        _dup2 = Guid.NewGuid();
        _isolated = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _tree, "Tools Tree", isDefault: true);

        await SeedPersonAsync(connection, _grandpa, "Grandpa", 'M');
        await SeedPersonAsync(connection, _grandma, "Grandma", 'F');
        await SeedPersonAsync(connection, _father, "Father", 'M');
        await SeedPersonAsync(connection, _mother, "Mother", 'F');
        await SeedPersonAsync(connection, _aunt, "Aunt", 'F');
        await SeedPersonAsync(connection, _child, "Child", 'M');
        await SeedPersonAsync(connection, _dup1, "Same Name", null);
        await SeedPersonAsync(connection, _dup2, "Same Name", null);
        await SeedPersonAsync(connection, _isolated, "Isolated Person", null);

        await TestSeeding.InsertParentChildAsync(connection, _tree, _grandpa, _father);
        await TestSeeding.InsertParentChildAsync(connection, _tree, _grandma, _father);
        await TestSeeding.InsertParentChildAsync(connection, _tree, _grandpa, _aunt);
        await TestSeeding.InsertParentChildAsync(connection, _tree, _grandma, _aunt);
        await TestSeeding.InsertParentChildAsync(connection, _tree, _father, _child);
        await TestSeeding.InsertParentChildAsync(connection, _tree, _mother, _child);

        await TestSeeding.InsertFamilyAsync(connection, _tree, _familyGrandparents, _grandpa, _grandma, marriageYear: 1925);
        await TestSeeding.InsertFamilyAsync(connection, _tree, _familyParents, _father, _mother, marriageYear: 1953);

        await InsertEventAsync(connection, _grandpa, "BIRT", yearFrom: 1900, placeRaw: "Kyiv");
        await InsertEventAsync(connection, _grandma, "BIRT", yearFrom: 1905, placeRaw: "Kyiv");
        await InsertEventAsync(connection, _father, "BIRT", yearFrom: 1930, placeRaw: "Poltava");
        await InsertEventAsync(connection, _mother, "BIRT", yearFrom: 1932, placeRaw: "Kharkiv");
        await InsertEventAsync(connection, _aunt, "BIRT", yearFrom: 1928, placeRaw: "Poltava");
        await InsertEventAsync(
            connection, _child, "BIRT", yearFrom: 1955,
            dateRaw: "3 MAR 1955", placeRaw: "Poltava guberniya, Poltava uyezd, village X");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    // ── get_person_family_context ────────────────────────────────────────────

    [Fact]
    public async Task GetPersonFamilyContext_Father_HasEventsParentsSiblingsMarriagesChildren()
    {
        var json = await _tools.GetPersonFamilyContextAsync("Father");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal("Father", root.GetProperty("person").GetProperty("fullName").GetString());
        Assert.True(root.GetProperty("events").GetArrayLength() > 0);
        Assert.Equal(2, root.GetProperty("parents").GetArrayLength());
        Assert.Equal(1, root.GetProperty("siblings").GetArrayLength());
        Assert.Equal(1, root.GetProperty("marriages").GetArrayLength());
        Assert.Equal(1, root.GetProperty("children").GetArrayLength());

        var sibling = root.GetProperty("siblings")[0];
        Assert.Equal("Aunt", sibling.GetProperty("fullName").GetString());

        var marriage = root.GetProperty("marriages")[0];
        Assert.Equal("Mother", marriage.GetProperty("spouseName").GetString());
    }

    // ── get_ancestors ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAncestors_Child_ReturnsAllFourAncestors()
    {
        var json = await _tools.GetAncestorsAsync("Child", maxGenerations: 5);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.GetProperty("count").GetInt32() > 0);
        Assert.Equal(4, root.GetProperty("ancestors").GetArrayLength());

        var names = root.GetProperty("ancestors").EnumerateArray()
            .Select(a => a.GetProperty("fullName").GetString())
            .ToList();
        Assert.Contains("Father", names);
        Assert.Contains("Mother", names);
        Assert.Contains("Grandpa", names);
        Assert.Contains("Grandma", names);
    }

    // ── get_descendants ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetDescendants_Grandpa_ReturnsFatherAuntAndChild()
    {
        var json = await _tools.GetDescendantsAsync("Grandpa", maxGenerations: 5);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(3, root.GetProperty("descendants").GetArrayLength());

        var names = root.GetProperty("descendants").EnumerateArray()
            .Select(d => d.GetProperty("fullName").GetString())
            .ToList();
        Assert.Contains("Father", names);
        Assert.Contains("Aunt", names);
        Assert.Contains("Child", names);

        // No DNA-linked fields anywhere in this product-neutral server.
        Assert.DoesNotContain("linkedDna", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── get_closest_common_ancestor ───────────────────────────────────────────

    [Fact]
    public async Task GetClosestCommonAncestor_ChildAndAunt_ResolvesToGrandparent()
    {
        var json = await _tools.GetClosestCommonAncestorAsync("Child", "Aunt");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        var ancestor = root.GetProperty("ancestor");
        Assert.NotEqual(JsonValueKind.Null, ancestor.ValueKind);

        var ancestorName = ancestor.GetProperty("ancestorName").GetString();
        Assert.True(ancestorName is "Grandpa" or "Grandma");

        Assert.Equal(2, root.GetProperty("personDepths").GetArrayLength());
    }

    // ── get_path_between_persons ─────────────────────────────────────────────

    [Fact]
    public async Task GetPathBetweenPersons_ChildAndAunt_ReturnsThreeSteps()
    {
        var json = await _tools.GetPathBetweenPersonsAsync("Child", "Aunt");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(3, root.GetProperty("stepCount").GetInt32());
        Assert.Equal(3, root.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task GetPathBetweenPersons_Unconnected_ReturnsEmptySteps()
    {
        var json = await _tools.GetPathBetweenPersonsAsync("Child", "Isolated Person");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(0, root.GetProperty("stepCount").GetInt32());
        Assert.Equal(0, root.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task GetPathBetweenPersons_AmbiguousName_ReturnsErrorWithCandidates()
    {
        var json = await _tools.GetPathBetweenPersonsAsync("Child", "Same Name");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out _));
        Assert.True(root.TryGetProperty("candidates", out var candidates));
        Assert.Equal(2, candidates.GetArrayLength());
    }

    // ── find_tree_person ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindTreePerson_ByName_ReturnsMatch()
    {
        var json = await _tools.FindTreePersonAsync("Father");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.GetProperty("count").GetInt32() >= 1);

        var names = root.GetProperty("people").EnumerateArray()
            .Select(p => p.GetProperty("fullName").GetString())
            .ToList();
        Assert.Contains("Father", names);
    }

    [Fact]
    public async Task FindTreePerson_WithFatherConstraint_FiltersCorrectly()
    {
        var json = await _tools.FindTreePersonAsync("Child", father: "Father");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(1, root.GetProperty("count").GetInt32());
    }

    // ── multi-match person resolution ────────────────────────────────────────

    [Fact]
    public async Task GetPersonFamilyContext_AmbiguousName_ReturnsErrorWithCandidates()
    {
        var json = await _tools.GetPersonFamilyContextAsync("Same Name");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
        Assert.True(root.TryGetProperty("candidates", out var candidates));
        Assert.Equal(2, candidates.GetArrayLength());

        // Never a silent pick: no "person" key should be present alongside the error.
        Assert.False(root.TryGetProperty("person", out _));
    }

    // ── unresolvable tree ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAncestors_UnknownTree_ReturnsError()
    {
        var json = await _tools.GetAncestorsAsync("Child", tree: "Nonexistent Tree XYZ");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
        Assert.False(root.TryGetProperty("ancestors", out _));
    }

    // ── get_tree_person ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetTreePerson_ByName_ReturnsPersonDetails()
    {
        var json = await _tools.GetTreePersonAsync("Grandpa");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal("Grandpa", root.GetProperty("person").GetProperty("fullName").GetString());
        Assert.Equal("M", root.GetProperty("person").GetProperty("sex").GetString());
    }

    // ── get_person_life_events ───────────────────────────────────────────────

    [Fact]
    public async Task GetPersonLifeEvents_Child_ReturnsBirthEventWithRawText()
    {
        var json = await _tools.GetPersonLifeEventsAsync("Child");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.GetProperty("eventCount").GetInt32() >= 1);

        var birthEvent = root.GetProperty("events").EnumerateArray()
            .First(e => e.GetProperty("eventType").GetString() == "BIRT");
        Assert.Equal("3 MAR 1955", birthEvent.GetProperty("dateRaw").GetString());
        Assert.Contains("Poltava guberniya", birthEvent.GetProperty("placeRaw").GetString());
    }

    // ── get_descendants_by_year ───────────────────────────────────────────────

    [Fact]
    public async Task GetDescendantsByYear_Grandpa_1953_IncludesMotherAsSpouse()
    {
        var json = await _tools.GetDescendantsByYearAsync("Grandpa", year: 1953, maxGenerations: 5);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        var people = root.GetProperty("people").EnumerateArray().ToList();
        Assert.Contains(people, p => p.GetProperty("fullName").GetString() == "Mother"
            && p.GetProperty("personType").GetString() == "SPOUSE");
    }

    // ── get_person_tree ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPersonTree_Descendants_FromGrandpa_IncludesChild()
    {
        var json = await _tools.GetPersonTreeAsync("Grandpa", direction: "descendants", maxGenerations: 5);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal("Grandpa", root.GetProperty("rootPerson").GetProperty("fullName").GetString());

        var names = root.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("fullName").GetString())
            .ToList();
        Assert.Contains("Child", names);

        Assert.DoesNotContain("linkedDna", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── create_tree_dataset ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateTreeDataset_NewName_ReturnsCreatedTree()
    {
        var json = await _tools.CreateTreeDatasetAsync("Brand New Tree", "a description");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal("Brand New Tree", root.GetProperty("tree").GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateTreeDataset_DuplicateName_ReturnsError()
    {
        var json = await _tools.CreateTreeDatasetAsync("Tools Tree");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrEmpty(error.GetString()));
    }

    // ── seeding helpers ───────────────────────────────────────────────────────

    private async Task SeedPersonAsync(NpgsqlConnection connection, Guid personId, string fullName, char? sex)
    {
        await TestSeeding.InsertPersonAsync(
            connection, _tree, personId, primaryDisplayName: fullName, sex: sex);
        await TestSeeding.InsertPersonNameAsync(
            connection, _tree, personId,
            scriptCode: "LAT", nameType: "birth",
            fullName: fullName, fullNameNormalized: fullName.ToLowerInvariant(),
            isPrimary: true);
    }

    /// <summary>
    /// Local seed helper for <c>genealogy.event</c> rows (with an optional
    /// place, created on demand). Kept in this test file rather than shared
    /// <c>TestSeeding</c> to avoid touching files other Phase 5 tasks own —
    /// mirrors the identical helper in <c>TreeTraversalTests</c>.
    /// </summary>
    private async Task InsertEventAsync(
        NpgsqlConnection connection,
        Guid personId,
        string eventType,
        int? yearFrom = null,
        string? dateRaw = null,
        string? placeRaw = null,
        CancellationToken cancellationToken = default)
    {
        long? placeId = null;
        if (placeRaw is not null)
        {
            const string placeSql = """
                INSERT INTO genealogy.place (place_raw)
                VALUES (@place_raw)
                ON CONFLICT (place_raw) DO UPDATE SET place_raw = EXCLUDED.place_raw
                RETURNING place_id;
                """;
            await using var placeCommand = new NpgsqlCommand(placeSql, connection);
            placeCommand.Parameters.Add(new NpgsqlParameter("place_raw", NpgsqlDbType.Text) { Value = placeRaw });
            placeId = (long)(await placeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        const string sql = """
            INSERT INTO genealogy.event
                (tree_id, person_id, event_type, date_raw, year_from, place_id)
            VALUES
                (@tree_id, @person_id, @event_type, @date_raw, @year_from, @place_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = _tree });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = eventType });
        command.Parameters.Add(new NpgsqlParameter("date_raw", NpgsqlDbType.Text) { Value = (object?)dateRaw ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("year_from", NpgsqlDbType.Smallint) { Value = (object?)(short?)yearFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("place_id", NpgsqlDbType.Bigint) { Value = (object?)placeId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
