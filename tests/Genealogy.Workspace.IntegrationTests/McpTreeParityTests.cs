using System.Text.Json;
using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Staging;
using Genealogy.Workspace.Data.Traversal;
using Genealogy.Workspace.McpServer.Tools;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 5 Task 6 (final) end-to-end exit criterion: proves the
/// <see cref="TreeTools"/> query surface agrees *semantically* with the
/// applied tree structure, exercised through the REAL
/// stage -&gt; apply -&gt; traverse pipeline (real python-parsed
/// <c>phase0_baseline.ged</c>, real <see cref="GedcomStagingService"/> /
/// <see cref="GedcomApplyService"/> apply with <c>dryRun:false</c>, real
/// <see cref="TreeTools"/> calls) rather than hand-seeded staging rows or a
/// synthetic in-memory tree.
///
/// docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10 Phase 5 exit criteria
/// covered here:
/// <list type="bullet">
/// <item>"golden source queries and PostgreSQL MCP results agree
/// semantically" — every assertion below is checked against an INDEPENDENT
/// oracle: either a fresh, separately-written SQL query against
/// <c>genealogy.parent_child</c>/<c>genealogy.family</c>/<c>genealogy.person</c>
/// (not a re-invocation of <see cref="TreeTraversalRepository"/>'s SQL), or a
/// plain-C# breadth-first walk over the tree edges fetched from those same
/// production tables. No expected count/id is hardcoded from reading the
/// fixture by eye.</item>
/// <item>"tests cover ... multiple trees" — <see cref="CrossTreeIsolation_SameFixtureAppliedToTwoTrees_NeverLeaksBetweenTrees"/>
/// applies the identical fixture into two independent trees (deterministic,
/// tree-scoped UUIDv5 ids means the same conceptual person gets two DIFFERENT
/// ids, one per tree — verified below) and proves tree-scoped
/// <c>get_ancestors</c>/<c>find_tree_person</c> on tree A never returns a
/// tree-B id.</item>
/// </list>
/// Deliberately NOT repeated here (already covered elsewhere per the task
/// brief): per-tool multi-match "{error,candidates}", unresolvable-tree
/// "{error}", no-DNA-leak (<see cref="TreeToolsTests"/>); a synthetic cycle at
/// the repository level (<see cref="TreeTraversalTests"/>); gedcom tool JSON
/// shapes (<see cref="GedcomToolsTests"/>).
///
/// The fixture (see <c>phase0_baseline.ged</c>, 28 persons / 8 families / 4
/// generations) is anchored on I1 "Гнат Романович Тестенко" x I2 "Килина
/// Тимофіївна Прикладів" (gen 0). Their son I3 "Северин Гнатович Тестенко"
/// (gen 1) married twice (I6 "Zofia Kowalska", I9 "Килина Опанасівна
/// Прикладів") producing two lines that both reach the same gen-3 depth:
/// I3 -&gt; I7 "Іван Тестенко" -&gt; I15 "Максим Іванович Тестенко", and
/// I3 -&gt; I10 "Роман Северинович Тестенко" -&gt; I18 "Петро Романович
/// Тестенко" — so I15 and I18 are first cousins whose closest common
/// ancestor is I3 (not the more distant I1/I2), used below as the
/// closest-common-ancestor / path-length case.
/// </summary>
public sealed class McpTreeParityTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private string _fixtureGedcomPath = string.Empty;

    private GedcomStagingService _stagingService = null!;
    private GedcomApplyService _applyService = null!;
    private TreeTools _treeTools = null!;

    public McpTreeParityTests(WorkspaceEnvironmentFixture fixture)
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

        _treeTools = new TreeTools(
            new TreeRepository(connectionFactory),
            new PersonRepository(connectionFactory),
            new PersonSearchRepository(connectionFactory),
            new RichFamilyContextRepository(connectionFactory),
            new PersonEventsRepository(connectionFactory),
            new TreeTraversalRepository(connectionFactory),
            new TreeResolver(connectionFactory),
            new PersonResolver(connectionFactory));

        _fixtureGedcomPath = Path.Combine(
            _fixture.WorkspaceDirectory, "tools", "gedcom", "tests", "fixtures", "phase0_baseline.ged");
        Assert.True(File.Exists(_fixtureGedcomPath), $"Fixture not found: {_fixtureGedcomPath}");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task EndToEnd_AppliedPhase0Baseline_TreeToolsAgreeWithIndependentOracle_AcrossFiveTools()
    {
        var tree = Guid.NewGuid();
        await SeedTreeAsync(tree, "Parity Tree", isDefault: true);

        var staged = await _stagingService.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = _fixtureGedcomPath,
            TreeId = tree,
        });
        var applied = await _applyService.ApplyAsync(staged.BatchId, dryRun: false);
        Assert.Equal("APPLIED", applied.Status);

        var treeIdStr = tree.ToString();

        // Discover the four persons we need through the REAL find_tree_person
        // tool (not hardcoded UUIDs) — each substring is unique in the fixture.
        var idMaksym = await DiscoverPersonIdAsync("Максим Іванович", treeIdStr);   // I15, gen-3 descendant of I1 via I7
        var idPetro = await DiscoverPersonIdAsync("Петро Романович", treeIdStr);    // I18, gen-3 descendant of I1 via I10
        var idHnat = await DiscoverPersonIdAsync("Гнат Романович", treeIdStr);      // I1, the tree's root couple
        var idSeveryn = await DiscoverPersonIdAsync("Северин Гнатович", treeIdStr); // I3, Гнат's son, both cousins' grandparent

        // The independent oracle: every parent_child edge in this tree, fetched
        // fresh and walked in plain C# (a different implementation strategy
        // entirely from TreeTraversalRepository's recursive SQL).
        var edges = await FetchParentChildEdgesAsync(tree);

        // ── (a) get_ancestors ────────────────────────────────────────────
        var oracleAncestorsMaksym = BuildAncestorDepths(edges, idMaksym);

        var ancestorsJson = await _treeTools.GetAncestorsAsync(idMaksym.ToString(), maxGenerations: 10, tree: treeIdStr);
        using (var doc = JsonDocument.Parse(ancestorsJson))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("error", out _), ancestorsJson);
            Assert.Equal(oracleAncestorsMaksym.Count, root.GetProperty("count").GetInt32());

            var rows = root.GetProperty("ancestors").EnumerateArray().ToList();
            Assert.Equal(oracleAncestorsMaksym.Count, rows.Count);

            var toolIds = rows.Select(r => r.GetProperty("personId").GetGuid()).ToHashSet();
            Assert.Equal(oracleAncestorsMaksym.Keys.ToHashSet(), toolIds);

            foreach (var row in rows)
            {
                var id = row.GetProperty("personId").GetGuid();
                Assert.Equal(oracleAncestorsMaksym[id], row.GetProperty("generation").GetInt32());
            }

            Assert.Equal(oracleAncestorsMaksym.Values.Max(), rows.Max(r => r.GetProperty("generation").GetInt32()));
        }

        // ── (b) get_descendants ──────────────────────────────────────────
        var oracleDescendantsHnat = BuildDescendantSet(edges, idHnat);

        var descendantsJson = await _treeTools.GetDescendantsAsync(idHnat.ToString(), maxGenerations: 10, tree: treeIdStr);
        using (var doc = JsonDocument.Parse(descendantsJson))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("error", out _), descendantsJson);
            Assert.Equal(oracleDescendantsHnat.Count, root.GetProperty("count").GetInt32());

            var toolIds = root.GetProperty("descendants").EnumerateArray()
                .Select(d => d.GetProperty("personId").GetGuid())
                .ToHashSet();
            Assert.Equal(oracleDescendantsHnat, toolIds);
        }

        // ── (c) get_closest_common_ancestor ───────────────────────────────
        var oracleAncestorsPetro = BuildAncestorDepths(edges, idPetro);
        var (oracleAncestorId, oracleDepthMaksym, oracleDepthPetro) =
            FindClosestCommonAncestor(oracleAncestorsMaksym, oracleAncestorsPetro);

        // Sanity check on the oracle itself against the fixture's known shape:
        // I15 and I18's closest common ancestor is their shared grandfather I3,
        // NOT the more distant great-grandparents I1/I2.
        Assert.Equal(idSeveryn, oracleAncestorId);
        Assert.Equal(2, oracleDepthMaksym);
        Assert.Equal(2, oracleDepthPetro);

        var ccaJson = await _treeTools.GetClosestCommonAncestorAsync(idMaksym.ToString(), idPetro.ToString(), tree: treeIdStr);
        using (var doc = JsonDocument.Parse(ccaJson))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("error", out _), ccaJson);

            var ancestor = root.GetProperty("ancestor");
            Assert.NotEqual(JsonValueKind.Null, ancestor.ValueKind);
            Assert.Equal(oracleAncestorId, ancestor.GetProperty("ancestorId").GetGuid());
            Assert.Equal(Math.Max(oracleDepthMaksym, oracleDepthPetro), ancestor.GetProperty("maxDepth").GetInt32());

            var depths = root.GetProperty("personDepths").EnumerateArray()
                .ToDictionary(d => d.GetProperty("personId").GetGuid(), d => d.GetProperty("depth").GetInt32());
            Assert.Equal(oracleDepthMaksym, depths[idMaksym]);
            Assert.Equal(oracleDepthPetro, depths[idPetro]);
        }

        // ── (d) get_path_between_persons ──────────────────────────────────
        var oracleExpectedPathLength = oracleDepthMaksym + oracleDepthPetro;

        var pathJson = await _treeTools.GetPathBetweenPersonsAsync(idMaksym.ToString(), idPetro.ToString(), tree: treeIdStr);
        using (var doc = JsonDocument.Parse(pathJson))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("error", out _), pathJson);
            Assert.Equal(oracleExpectedPathLength, root.GetProperty("stepCount").GetInt32());
            Assert.Equal(oracleExpectedPathLength, root.GetProperty("steps").GetArrayLength());
        }

        // ── (e) get_person_family_context ─────────────────────────────────
        var oracleParentsCount = await ScalarIntAsync(
            "SELECT count(*) FROM genealogy.parent_child WHERE tree_id = @tree AND child_person_id = @person;",
            tree, idSeveryn);
        var oracleChildrenIds = await FetchChildrenIdsOracleAsync(tree, idSeveryn);
        var oracleSiblingsCount = await ScalarIntAsync(
            """
            SELECT count(DISTINCT sib.child_person_id)
            FROM genealogy.parent_child me
            JOIN genealogy.parent_child sib
                ON sib.tree_id = me.tree_id
               AND sib.parent_person_id = me.parent_person_id
               AND sib.child_person_id <> @person
            WHERE me.tree_id = @tree AND me.child_person_id = @person;
            """,
            tree, idSeveryn);
        var oracleSpouseIds = await FetchSpouseIdsOracleAsync(tree, idSeveryn);

        // Sanity check on the oracle against the fixture's known shape: I3
        // ("Северин Гнатович Тестенко") has 2 parents (I1, I2), 3 siblings
        // (I4, I5, I21), married twice (I6, I9) and has 4 children total
        // (I7, I8, I20 via I6; I10 via I9).
        Assert.Equal(2, oracleParentsCount);
        Assert.Equal(3, oracleSiblingsCount);
        Assert.Equal(2, oracleSpouseIds.Count);
        Assert.Equal(4, oracleChildrenIds.Count);

        var contextJson = await _treeTools.GetPersonFamilyContextAsync(idSeveryn.ToString(), tree: treeIdStr);
        using (var doc = JsonDocument.Parse(contextJson))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("error", out _), contextJson);

            Assert.Equal(oracleParentsCount, root.GetProperty("parents").GetArrayLength());
            Assert.Equal(oracleSiblingsCount, root.GetProperty("siblings").GetArrayLength());

            var toolChildrenIds = root.GetProperty("children").EnumerateArray()
                .Select(c => c.GetProperty("personId").GetGuid())
                .ToHashSet();
            Assert.Equal(oracleChildrenIds.ToHashSet(), toolChildrenIds);

            var marriages = root.GetProperty("marriages").EnumerateArray().ToList();
            Assert.Equal(oracleSpouseIds.Count, marriages.Count);
            var toolSpouseIds = marriages.Select(m => m.GetProperty("spousePersonId").GetGuid()).ToHashSet();
            Assert.Equal(oracleSpouseIds.ToHashSet(), toolSpouseIds);
        }
    }

    [Fact]
    public async Task CrossTreeIsolation_SameFixtureAppliedToTwoTrees_NeverLeaksBetweenTrees()
    {
        var treeA = Guid.NewGuid();
        var treeB = Guid.NewGuid();
        await SeedTreeAsync(treeA, "Isolation Tree A");
        await SeedTreeAsync(treeB, "Isolation Tree B");

        var stagedA = await _stagingService.StageAsync(new GedcomStageRequest { GedcomFilePath = _fixtureGedcomPath, TreeId = treeA });
        Assert.Equal("APPLIED", (await _applyService.ApplyAsync(stagedA.BatchId, dryRun: false)).Status);

        var stagedB = await _stagingService.StageAsync(new GedcomStageRequest { GedcomFilePath = _fixtureGedcomPath, TreeId = treeB });
        Assert.Equal("APPLIED", (await _applyService.ApplyAsync(stagedB.BatchId, dryRun: false)).Status);

        var treeAStr = treeA.ToString();
        var treeBStr = treeB.ToString();

        // Same conceptual person, discovered independently in each tree via the
        // real find_tree_person tool — must resolve to two DIFFERENT ids
        // (tree-scoped deterministic UUIDv5), never the same id.
        var idMaksymA = await DiscoverPersonIdAsync("Максим Іванович", treeAStr);
        var idMaksymB = await DiscoverPersonIdAsync("Максим Іванович", treeBStr);
        Assert.NotEqual(idMaksymA, idMaksymB);

        var allIdsA = await FetchAllPersonIdsAsync(treeA);
        var allIdsB = await FetchAllPersonIdsAsync(treeB);
        Assert.Equal(28, allIdsA.Count);
        Assert.Equal(28, allIdsB.Count);
        Assert.Empty(allIdsA.Intersect(allIdsB)); // sanity: no accidental id collision between trees

        // get_ancestors scoped to tree A must return ONLY tree-A ids.
        var edgesA = await FetchParentChildEdgesAsync(treeA);
        var oracleAncestorsA = BuildAncestorDepths(edgesA, idMaksymA);

        var ancestorsAJson = await _treeTools.GetAncestorsAsync(idMaksymA.ToString(), maxGenerations: 10, tree: treeAStr);
        using (var doc = JsonDocument.Parse(ancestorsAJson))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("error", out _), ancestorsAJson);

            var toolIds = root.GetProperty("ancestors").EnumerateArray()
                .Select(r => r.GetProperty("personId").GetGuid())
                .ToHashSet();

            Assert.Equal(oracleAncestorsA.Count, toolIds.Count);
            Assert.True(toolIds.IsSubsetOf(allIdsA), "leak: get_ancestors on tree A returned an id outside tree A's person set");
            Assert.True(toolIds.Intersect(allIdsB).Count() == 0, "leak: get_ancestors on tree A returned a tree-B person id");
        }

        // find_tree_person scoped to tree A must return ONLY tree-A ids,
        // matching an independent surname oracle exactly.
        var (oracleCountA, oracleIdsA) = await CountAndListSurnameOracleAsync(treeA, "тестенко");
        Assert.Equal(13, oracleCountA); // I1,I3,I4,I5,I7,I8,I10,I15,I16,I18,I19,I20,I21

        var findJson = await _treeTools.FindTreePersonAsync("Тестенко", tree: treeAStr);
        using (var doc = JsonDocument.Parse(findJson))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("error", out _), findJson);
            Assert.Equal(oracleCountA, root.GetProperty("count").GetInt32());

            var toolIds = root.GetProperty("people").EnumerateArray()
                .Select(p => p.GetProperty("personId").GetGuid())
                .ToHashSet();

            Assert.Equal(oracleIdsA.ToHashSet(), toolIds);
            Assert.True(toolIds.IsSubsetOf(allIdsA), "leak: find_tree_person on tree A returned an id outside tree A's person set");
            Assert.True(toolIds.Intersect(allIdsB).Count() == 0, "leak: find_tree_person on tree A returned a tree-B person id");
        }
    }

    // ── seeding / discovery helpers ──────────────────────────────────────────

    private async Task SeedTreeAsync(Guid treeId, string name, bool isDefault = false)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await TestSeeding.InsertTreeAsync(connection, treeId, name, isDefault: isDefault);
    }

    /// <summary>
    /// Resolves a person id through the real <c>find_tree_person</c> tool
    /// rather than a hardcoded UUID or a raw SQL lookup — this is how the test
    /// discovers the deterministic ids it needs at runtime.
    /// </summary>
    private async Task<Guid> DiscoverPersonIdAsync(string nameSubstring, string treeIdOrName)
    {
        var json = await _treeTools.FindTreePersonAsync(nameSubstring, tree: treeIdOrName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _), json);

        var people = root.GetProperty("people").EnumerateArray().ToList();
        Assert.True(
            people.Count == 1,
            $"Expected exactly one find_tree_person match for '{nameSubstring}' in tree {treeIdOrName}, got {people.Count}. json={json}");

        return people[0].GetProperty("personId").GetGuid();
    }

    // ── independent oracle: raw edges + plain-C# graph walk ──────────────────

    private async Task<List<(Guid Parent, Guid Child)>> FetchParentChildEdgesAsync(Guid treeId)
    {
        var edges = new List<(Guid, Guid)>();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT parent_person_id, child_person_id FROM genealogy.parent_child WHERE tree_id = @tree;", connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            edges.Add((reader.GetFieldValue<Guid>(0), reader.GetFieldValue<Guid>(1)));
        }

        return edges;
    }

    /// <summary>Breadth-first walk UP from <paramref name="personId"/> via child-&gt;parent edges. 1-based depth.</summary>
    private static Dictionary<Guid, int> BuildAncestorDepths(IReadOnlyList<(Guid Parent, Guid Child)> edges, Guid personId)
    {
        var parentsOf = edges.GroupBy(e => e.Child)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Parent).Distinct().ToList());

        var depths = new Dictionary<Guid, int>();
        var visited = new HashSet<Guid> { personId };
        var frontier = new Queue<(Guid Id, int Depth)>();
        frontier.Enqueue((personId, 0));

        while (frontier.Count > 0)
        {
            var (id, depth) = frontier.Dequeue();
            if (!parentsOf.TryGetValue(id, out var parents))
            {
                continue;
            }

            foreach (var parent in parents)
            {
                if (visited.Add(parent))
                {
                    depths[parent] = depth + 1;
                    frontier.Enqueue((parent, depth + 1));
                }
            }
        }

        return depths;
    }

    /// <summary>Breadth-first walk DOWN from <paramref name="ancestorId"/> via parent-&gt;child edges. Excludes the ancestor itself.</summary>
    private static HashSet<Guid> BuildDescendantSet(IReadOnlyList<(Guid Parent, Guid Child)> edges, Guid ancestorId)
    {
        var childrenOf = edges.GroupBy(e => e.Parent)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Child).Distinct().ToList());

        var visited = new HashSet<Guid> { ancestorId };
        var result = new HashSet<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(ancestorId);

        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();
            if (!childrenOf.TryGetValue(id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (visited.Add(child))
                {
                    result.Add(child);
                    frontier.Enqueue(child);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Picks the common ancestor with the smallest max-depth (ties broken by
    /// smallest summed depth, then id) across two ancestor-depth maps produced
    /// by <see cref="BuildAncestorDepths"/> — the same tie-break order as
    /// <c>TreeTraversalRepository.GetClosestCommonAncestorAsync</c>'s SQL, but
    /// implemented independently in plain C# over in-memory dictionaries.
    /// </summary>
    private static (Guid AncestorId, int Depth1, int Depth2) FindClosestCommonAncestor(
        IReadOnlyDictionary<Guid, int> depths1, IReadOnlyDictionary<Guid, int> depths2)
    {
        var common = depths1.Keys.Intersect(depths2.Keys)
            .Select(id => (Id: id, D1: depths1[id], D2: depths2[id]))
            .OrderBy(x => Math.Max(x.D1, x.D2))
            .ThenBy(x => x.D1 + x.D2)
            .ThenBy(x => x.Id)
            .ToList();

        Assert.True(common.Count > 0, "oracle found no common ancestor between the two persons");

        var best = common[0];
        return (best.Id, best.D1, best.D2);
    }

    // ── independent oracle: fresh, hand-written SQL ───────────────────────────

    private async Task<int> ScalarIntAsync(string sql, Guid treeId, Guid personId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });
        return checked((int)(long)(await command.ExecuteScalarAsync())!);
    }

    private async Task<List<Guid>> FetchChildrenIdsOracleAsync(Guid treeId, Guid personId)
    {
        var ids = new List<Guid>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT child_person_id FROM genealogy.parent_child WHERE tree_id = @tree AND parent_person_id = @person;",
            connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetFieldValue<Guid>(0));
        }

        return ids;
    }

    private async Task<List<Guid>> FetchSpouseIdsOracleAsync(Guid treeId, Guid personId)
    {
        var ids = new List<Guid>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT CASE WHEN spouse1_person_id = @person THEN spouse2_person_id ELSE spouse1_person_id END
            FROM genealogy.family
            WHERE tree_id = @tree AND (spouse1_person_id = @person OR spouse2_person_id = @person);
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetFieldValue<Guid>(0));
        }

        return ids;
    }

    private async Task<HashSet<Guid>> FetchAllPersonIdsAsync(Guid treeId)
    {
        var ids = new HashSet<Guid>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT person_id FROM genealogy.person WHERE tree_id = @tree;", connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetFieldValue<Guid>(0));
        }

        return ids;
    }

    private async Task<(int Count, List<Guid> Ids)> CountAndListSurnameOracleAsync(Guid treeId, string surnameNormalized)
    {
        var ids = new List<Guid>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT person_id FROM genealogy.person WHERE tree_id = @tree AND surname_normalized = @surname;", connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("surname", NpgsqlDbType.Text) { Value = surnameNormalized });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetFieldValue<Guid>(0));
        }

        return (ids.Count, ids);
    }
}
