using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Traversal;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 5 Task 1: exercises <see cref="TreeTraversalRepository"/> against a
/// fresh, migrated database seeded with a small deterministic tree. Mirrors the
/// <see cref="DatabaseLifecycleTests"/>/<see cref="FamilyContextTests"/> fixture
/// pattern: each test class instance gets its own database created in
/// <see cref="InitializeAsync"/> and force-dropped in <see cref="DisposeAsync"/>.
///
/// The seeded tree (all in <c>_tree</c>):
///
///   gen 0:                 great-grandpa (M, b.1900)
///                                 |
///   gen 1:                grandpa (M, b.1925, d.1990) == grandma (F, b.1928)
///                                 |
///   gen 2:      father (M, b.1950) == mother (F, no birth/death years at all)
///                                 |                    (spouse-with-no-years case)
///   gen 3:            child (M, b.1980)          aunt (F, b.1955) -- father's sibling
///
///   plus great-grandma-maternal (F, b.1905) -> mother, giving `child` and
///   `aunt` an asymmetric-depth common ancestor test via father/mother lines.
///
///   plus a disconnected synthetic cycle: cycleA -> cycleB -> cycleC -> cycleA
///   (parent_child edges) used only to prove traversal termination.
/// </summary>
public sealed class TreeTraversalTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private TreeTraversalRepository _repository = null!;

    private Guid _tree;

    private Guid _greatGrandpa;
    private Guid _grandpa;
    private Guid _grandma;
    private Guid _greatGrandmaMaternal;
    private Guid _father;
    private Guid _mother;
    private Guid _child;
    private Guid _aunt;

    private Guid _familyGrandparents;
    private Guid _familyParents;

    private Guid _cycleA;
    private Guid _cycleB;
    private Guid _cycleC;

    public TreeTraversalTests(WorkspaceEnvironmentFixture fixture)
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

        _repository = new TreeTraversalRepository(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        _tree = Guid.NewGuid();
        _greatGrandpa = Guid.NewGuid();
        _grandpa = Guid.NewGuid();
        _grandma = Guid.NewGuid();
        _greatGrandmaMaternal = Guid.NewGuid();
        _father = Guid.NewGuid();
        _mother = Guid.NewGuid();
        _child = Guid.NewGuid();
        _aunt = Guid.NewGuid();
        _familyGrandparents = Guid.NewGuid();
        _familyParents = Guid.NewGuid();
        _cycleA = Guid.NewGuid();
        _cycleB = Guid.NewGuid();
        _cycleC = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _tree, "Traversal Tree");

        await TestSeeding.InsertPersonAsync(connection, _tree, _greatGrandpa, primaryDisplayName: "Great Grandpa", sex: 'M');
        await TestSeeding.InsertPersonAsync(connection, _tree, _grandpa, primaryDisplayName: "Grandpa", sex: 'M');
        await TestSeeding.InsertPersonAsync(connection, _tree, _grandma, primaryDisplayName: "Grandma", sex: 'F');
        await TestSeeding.InsertPersonAsync(connection, _tree, _greatGrandmaMaternal, primaryDisplayName: "Great Grandma Maternal", sex: 'F');
        await TestSeeding.InsertPersonAsync(connection, _tree, _father, primaryDisplayName: "Father", sex: 'M');
        await TestSeeding.InsertPersonAsync(connection, _tree, _mother, primaryDisplayName: "Mother", sex: 'F');
        await TestSeeding.InsertPersonAsync(connection, _tree, _child, primaryDisplayName: "Child", sex: 'M');
        await TestSeeding.InsertPersonAsync(connection, _tree, _aunt, primaryDisplayName: "Aunt", sex: 'F');

        // Ancestry edges (parent -> child).
        await TestSeeding.InsertParentChildAsync(connection, _tree, _greatGrandpa, _grandpa, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _tree, _grandpa, _father, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _tree, _grandma, _father, "BIO");
        // Aunt is a child of grandpa only (not grandma) so the child->aunt
        // shortest path is unambiguous (child->father->grandpa->aunt).
        await TestSeeding.InsertParentChildAsync(connection, _tree, _grandpa, _aunt, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _tree, _father, _child, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _tree, _mother, _child, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _tree, _greatGrandmaMaternal, _mother, "BIO");

        // Marriages.
        await TestSeeding.InsertFamilyAsync(connection, _tree, _familyGrandparents, _grandpa, _grandma, marriageYear: 1948);
        // Father + Mother marriage; Mother deliberately has NO birth/death years
        // and the marriage year is left NULL to test unconditional spouse
        // inclusion via marriage.
        await TestSeeding.InsertFamilyAsync(connection, _tree, _familyParents, _father, _mother, marriageYear: null);

        // Events. Mother intentionally has no events at all.
        await InsertEventAsync(connection, _tree, _greatGrandpa, "BIRT", yearFrom: 1900, placeRaw: "Kyiv");
        await InsertEventAsync(connection, _tree, _greatGrandmaMaternal, "BIRT", yearFrom: 1905, placeRaw: "Lviv");
        await InsertEventAsync(connection, _tree, _grandpa, "BIRT", yearFrom: 1925, placeRaw: "Poltava");
        await InsertEventAsync(connection, _tree, _grandpa, "DEAT", yearFrom: 1990, placeRaw: "Poltava");
        await InsertEventAsync(connection, _tree, _grandma, "BIRT", yearFrom: 1928, placeRaw: "Poltava");
        await InsertEventAsync(connection, _tree, _father, "BIRT", yearFrom: 1950, placeRaw: "Kharkiv");
        await InsertEventAsync(connection, _tree, _aunt, "BIRT", yearFrom: 1955, placeRaw: "Kharkiv");
        await InsertEventAsync(connection, _tree, _child, "BIRT", yearFrom: 1980, placeRaw: "Kharkiv");

        // Synthetic cycle A -> B -> C -> A (disconnected from the main tree).
        await TestSeeding.InsertPersonAsync(connection, _tree, _cycleA, primaryDisplayName: "Cycle A", sex: 'M');
        await TestSeeding.InsertPersonAsync(connection, _tree, _cycleB, primaryDisplayName: "Cycle B", sex: 'F');
        await TestSeeding.InsertPersonAsync(connection, _tree, _cycleC, primaryDisplayName: "Cycle C", sex: 'M');
        await TestSeeding.InsertParentChildAsync(connection, _tree, _cycleA, _cycleB, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _tree, _cycleB, _cycleC, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _tree, _cycleC, _cycleA, "BIO");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    // ── GetAncestorsAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAncestors_Child_ReturnsThreeGenerationsWithBirthDeathEnrichment()
    {
        var ancestors = await _repository.GetAncestorsAsync(_tree, _child, maxGenerations: 6);

        // gen1: father, mother; gen2: grandpa, grandma, great-grandma-maternal;
        // gen3: great-grandpa.
        Assert.Equal(6, ancestors.Count);

        var father = Assert.Single(ancestors, a => a.PersonId == _father);
        Assert.Equal(1, father.Generation);
        Assert.Equal(1950, father.BirthYear);
        Assert.Equal("Kharkiv", father.BirthPlace);

        var grandpa = Assert.Single(ancestors, a => a.PersonId == _grandpa);
        Assert.Equal(2, grandpa.Generation);
        Assert.Equal(1925, grandpa.BirthYear);
        Assert.Equal(1990, grandpa.DeathYear);
        Assert.Equal("Poltava", grandpa.DeathPlace);

        var greatGrandpa = Assert.Single(ancestors, a => a.PersonId == _greatGrandpa);
        Assert.Equal(3, greatGrandpa.Generation);
        Assert.Equal(1900, greatGrandpa.BirthYear);

        // Mother has no events: present but with null birth/death.
        var mother = Assert.Single(ancestors, a => a.PersonId == _mother);
        Assert.Equal(1, mother.Generation);
        Assert.Null(mother.BirthYear);
        Assert.Null(mother.DeathYear);
    }

    [Fact]
    public async Task GetAncestors_RespectsMaxGenerations()
    {
        var ancestors = await _repository.GetAncestorsAsync(_tree, _child, maxGenerations: 1);

        Assert.Equal(2, ancestors.Count);
        Assert.All(ancestors, a => Assert.Equal(1, a.Generation));
        Assert.Contains(ancestors, a => a.PersonId == _father);
        Assert.Contains(ancestors, a => a.PersonId == _mother);
    }

    [Fact]
    public async Task GetAncestors_TerminatesOnSyntheticCycle()
    {
        // A -> B -> C -> A. Walking up from C: parent(C)=B (gen1),
        // parent(B)=A (gen2), parent(A)=C is already visited -> guard stops the
        // loop. So exactly B and A are returned (C is the start, not its own
        // ancestor) and the recursion terminates instead of running to the cap.
        var ancestors = await _repository.GetAncestorsAsync(_tree, _cycleC, maxGenerations: 50);

        Assert.Equal(2, ancestors.Count);
        Assert.Equal(new[] { _cycleA, _cycleB }.OrderBy(x => x),
            ancestors.Select(a => a.PersonId).OrderBy(x => x));
        Assert.DoesNotContain(ancestors, a => a.PersonId == _cycleC);
    }

    // ── GetDescendantsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetDescendants_Grandpa_ReturnsChildrenAndGrandchildren()
    {
        var descendants = await _repository.GetDescendantsAsync(_tree, _grandpa, maxGenerations: 8);

        // gen1: father, aunt; gen2: child.
        Assert.Equal(3, descendants.Count);

        var father = Assert.Single(descendants, d => d.PersonId == _father);
        Assert.Equal(1, father.Generation);
        Assert.Equal(_grandpa, father.ParentPersonId);

        var child = Assert.Single(descendants, d => d.PersonId == _child);
        Assert.Equal(2, child.Generation);
        Assert.Equal(_father, child.ParentPersonId);

        Assert.DoesNotContain(descendants, d => d.PersonId == _grandpa);
    }

    // ── GetDescendantsAtYearAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetDescendantsAtYear_IncludesSpouseWithNoYears_ExactlyOnce()
    {
        // In 1985: father (b.1950) is alive; child (b.1980) is alive. Mother is
        // father's spouse via _familyParents with NO birth/death years and a
        // NULL marriage year — she must be included as a SPOUSE row exactly once
        // (this is the duplication-fix + unconditional-inclusion assertion).
        var rows = await _repository.GetDescendantsAtYearAsync(_tree, _greatGrandpa, year: 1985, maxGenerations: 8);

        var motherRows = rows.Where(r => r.PersonId == _mother).ToList();
        Assert.Single(motherRows);
        Assert.Equal("SPOUSE", motherRows[0].PersonType);
        Assert.Equal(_father, motherRows[0].SpouseOfPersonId);
        Assert.Null(motherRows[0].BirthYear);
        Assert.Null(motherRows[0].DeathYear);

        // Father and child are alive descendants in 1985.
        Assert.Contains(rows, r => r.PersonId == _father && r.PersonType == "DESCENDANT");
        Assert.Contains(rows, r => r.PersonId == _child && r.PersonType == "DESCENDANT");
    }

    [Fact]
    public async Task GetDescendantsAtYear_ExcludesDescendantsNotYetBorn()
    {
        // In 1960: father (b.1950) and aunt (b.1955) are alive; child (b.1980)
        // is not yet born and must be excluded.
        var rows = await _repository.GetDescendantsAtYearAsync(_tree, _grandpa, year: 1960, maxGenerations: 8);

        Assert.Contains(rows, r => r.PersonId == _father && r.PersonType == "DESCENDANT");
        Assert.Contains(rows, r => r.PersonId == _aunt && r.PersonType == "DESCENDANT");
        Assert.DoesNotContain(rows, r => r.PersonId == _child && r.PersonType == "DESCENDANT");
    }

    // ── GetClosestCommonAncestorAsync ──────────────────────────────────────

    [Fact]
    public async Task GetClosestCommonAncestor_ChildAndAunt_AsymmetricDepth()
    {
        // child's ancestors include grandpa at depth 2 (child->father->grandpa).
        // aunt's ancestors include grandpa at depth 1 (aunt->grandpa).
        // grandpa is the closest common ancestor with asymmetric depths (2 vs 1).
        var result = await _repository.GetClosestCommonAncestorAsync(
            _tree, new[] { _child, _aunt }, maxDepth: 12);

        Assert.NotNull(result.Ancestor);
        Assert.Equal(_grandpa, result.Ancestor!.AncestorId);
        Assert.Equal(2, result.Ancestor.MaxDepth);   // max(2, 1)
        Assert.Equal(3, result.Ancestor.SumDepth);    // 2 + 1
        Assert.Equal(2, result.Ancestor.PersonCount);

        var childDepth = Assert.Single(result.InputDepths, d => d.PersonId == _child);
        Assert.Equal(2, childDepth.Depth);
        var auntDepth = Assert.Single(result.InputDepths, d => d.PersonId == _aunt);
        Assert.Equal(1, auntDepth.Depth);
    }

    [Fact]
    public async Task GetClosestCommonAncestor_NoCommonAncestor_ReturnsNullWinner()
    {
        var result = await _repository.GetClosestCommonAncestorAsync(
            _tree, new[] { _child, _cycleA }, maxDepth: 12);

        Assert.Null(result.Ancestor);
        Assert.Empty(result.InputDepths);
    }

    // ── GetPersonTreeAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetPersonTree_AncestorsWithRoot_BuildsEdgesAndPath()
    {
        var nodes = await _repository.GetPersonTreeAsync(
            _tree, _child, direction: "ancestors", maxGenerations: 6, includeRoot: true);

        var root = Assert.Single(nodes, n => n.PersonId == _child && n.Generation == 0);
        Assert.Null(root.EdgeFromPersonId);
        Assert.Equal(_child.ToString(), root.Path);

        var father = Assert.Single(nodes, n => n.PersonId == _father);
        Assert.Equal(1, father.Generation);
        Assert.Equal(_father, father.EdgeFromPersonId); // parent side of the edge
        Assert.Equal(_child, father.EdgeToPersonId);    // child side of the edge
        Assert.Equal($"{_child}>{_father}", father.Path);
        Assert.Equal(1950, father.BirthYear);
    }

    [Fact]
    public async Task GetPersonTree_ExcludeRoot_OmitsRootRow()
    {
        var nodes = await _repository.GetPersonTreeAsync(
            _tree, _child, direction: "ancestors", maxGenerations: 6, includeRoot: false);

        Assert.DoesNotContain(nodes, n => n.PersonId == _child && n.Generation == 0);
        Assert.All(nodes, n => Assert.True(n.Generation > 0));
        Assert.Contains(nodes, n => n.PersonId == _father);
    }

    [Fact]
    public async Task GetPersonTree_TerminatesOnSyntheticCycle()
    {
        var nodes = await _repository.GetPersonTreeAsync(
            _tree, _cycleA, direction: "ancestors", maxGenerations: 50, includeRoot: true);

        // Root A plus its two distinct reachable ancestors in the cycle (C, B).
        // Terminates cleanly rather than looping to the depth cap.
        Assert.Equal(3, nodes.Select(n => n.PersonId).Distinct().Count());
        Assert.True(nodes.Count <= 3);
    }

    // ── GetPathBetweenPersonsAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetPathBetween_ChildToAunt_ThreeStepPath()
    {
        // child -> father (CHILD_OF) -> grandpa (CHILD_OF) -> aunt (PARENT_OF).
        var steps = await _repository.GetPathBetweenPersonsAsync(_tree, _child, _aunt, maxDepth: 20);

        Assert.Equal(3, steps.Count);

        Assert.Equal(_child, steps[0].FromId);
        Assert.Equal(_father, steps[0].ToId);
        Assert.Equal("CHILD_OF", steps[0].Relation);

        Assert.Equal(_father, steps[1].FromId);
        Assert.Equal(_grandpa, steps[1].ToId);
        Assert.Equal("CHILD_OF", steps[1].Relation);

        Assert.Equal(_grandpa, steps[2].FromId);
        Assert.Equal(_aunt, steps[2].ToId);
        Assert.Equal("PARENT_OF", steps[2].Relation);
    }

    [Fact]
    public async Task GetPathBetween_Unconnected_ReturnsEmptyPath()
    {
        var steps = await _repository.GetPathBetweenPersonsAsync(_tree, _child, _cycleA, maxDepth: 20);
        Assert.Empty(steps);
    }

    /// <summary>
    /// Local seed helper for <c>genealogy.event</c> rows (with an optional
    /// place, created on demand). Kept in this test file rather than shared
    /// <c>TestSeeding</c> to avoid touching files other Phase 5 tasks own.
    /// </summary>
    private static async Task InsertEventAsync(
        NpgsqlConnection connection,
        Guid treeId,
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
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = eventType });
        command.Parameters.Add(new NpgsqlParameter("date_raw", NpgsqlDbType.Text) { Value = (object?)dateRaw ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("year_from", NpgsqlDbType.Smallint) { Value = yearFrom is null ? DBNull.Value : (short)yearFrom.Value });
        command.Parameters.Add(new NpgsqlParameter("place_id", NpgsqlDbType.Bigint) { Value = (object?)placeId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
