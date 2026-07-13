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
using Xunit.Abstractions;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 5 Task 6 (final), OPT-IN golden-diff spot check against a REAL
/// Rudenko.ged. This test is entirely gated on the
/// <c>GENEALOGY_RUDENKO_GED</c> environment variable (an absolute path to a
/// real, private Rudenko.ged): when it is unset, or points at a file that
/// does not exist — the expected state on a clean checkout and in CI — the
/// test logs a message via <see cref="ITestOutputHelper"/> and returns
/// immediately without touching Docker/PostgreSQL, so <c>dotnet test</c>
/// always passes here with zero cost. Real Rudenko genealogical data is
/// never committed to this repository and this file must never embed any of
/// it.
///
/// This project's <c>Genealogy.Workspace.IntegrationTests.csproj</c> only
/// references plain <c>xunit</c> + <c>xunit.runner.visualstudio</c> — no
/// <c>Xunit.SkippableFact</c> package is present — so this uses a runtime
/// "return early" skip rather than <c>[SkippableFact]</c>/<c>Assert.Skip</c>.
///
/// The full historical ~259-row golden diff against the legacy SQL Server
/// export is a manual/offline exercise and out of scope here. This test only
/// spot-checks that a couple of Phase-0 golden RELATIONSHIPS still hold
/// semantically once the real file is staged, applied, and queried through
/// the real <see cref="TreeTools"/> surface — robust counts/relationships,
/// not a brittle byte-diff. Because this file cannot see the real Rudenko.ged
/// content, the two spot checks are deliberately data-driven (discovered from
/// whatever the real file actually contains) rather than hardcoded to
/// specific Ukrainian names:
/// <list type="bullet">
/// <item>closest-common-ancestor: any two siblings (a family with &gt;= 2
/// children) resolve, via <c>get_closest_common_ancestor</c>, to their shared
/// parent at depth 1 each — verified against an independent oracle.</item>
/// <item>ancestors of "the root": the person with the deepest recorded
/// ancestor chain (the usual shape of a tree's main subject) returns a
/// plausible (&gt; 0, oracle-matching) <c>get_ancestors</c> count.</item>
/// </list>
/// </summary>
public sealed class RudenkoGoldenParityTests
{
    private readonly ITestOutputHelper _output;

    public RudenkoGoldenParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GoldenSpotCheck_RealRudenkoGedcom_ClosestCommonAncestorAndRootAncestorCount_HoldSemantically()
    {
        var gedcomPath = Environment.GetEnvironmentVariable("GENEALOGY_RUDENKO_GED");
        if (string.IsNullOrWhiteSpace(gedcomPath) || !File.Exists(gedcomPath))
        {
            _output.WriteLine(
                "skipped: set GENEALOGY_RUDENKO_GED to run (an absolute path to a real, private " +
                "Rudenko.ged). This is expected on a clean checkout and in CI — no Rudenko data is " +
                $"committed to this repository. GENEALOGY_RUDENKO_GED={(gedcomPath is null ? "<unset>" : gedcomPath)}");
            return;
        }

        var fixture = new WorkspaceEnvironmentFixture();
        await fixture.InitializeAsync();
        var databaseName = TestSeeding.NewTestDatabaseName();
        await fixture.CreateDatabaseAsync(databaseName);

        try
        {
            var connectionString = NpgsqlConnectionFactory.BuildConnectionString(
                fixture.BuildOptionsForDatabase(databaseName));
            new MigrationEngine(connectionString).Migrate();

            var connectionFactory = new NpgsqlConnectionFactory(fixture.BuildOptionsForDatabase(databaseName));
            var stagingService = new GedcomStagingService(connectionFactory);
            var applyService = new GedcomApplyService(connectionFactory);
            var treeTools = new TreeTools(
                new TreeRepository(connectionFactory),
                new PersonRepository(connectionFactory),
                new PersonSearchRepository(connectionFactory),
                new RichFamilyContextRepository(connectionFactory),
                new PersonEventsRepository(connectionFactory),
                new TreeTraversalRepository(connectionFactory),
                new TreeResolver(connectionFactory),
                new PersonResolver(connectionFactory));

            var treeId = Guid.NewGuid();
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await TestSeeding.InsertTreeAsync(connection, treeId, "Rudenko Golden Spot Check", isDefault: true);
            }

            var staged = await stagingService.StageAsync(new GedcomStageRequest
            {
                GedcomFilePath = gedcomPath,
                TreeId = treeId,
                LegacyIds = true,
            });
            _output.WriteLine($"Staged {gedcomPath}: {JsonSerializer.Serialize(staged.RowCounts)}");

            var applied = await applyService.ApplyAsync(staged.BatchId, dryRun: false);
            Assert.Equal("APPLIED", applied.Status);

            var edges = await FetchParentChildEdgesAsync(connectionString, treeId);
            Assert.True(edges.Count > 0, "Rudenko.ged produced zero parent_child edges after apply — nothing to spot-check.");

            var treeIdStr = treeId.ToString();

            // ── Spot check 1: closest common ancestor of two real siblings ──
            var siblingPair = await FindAnySiblingPairAsync(connectionString, treeId);
            Assert.True(
                siblingPair.HasValue,
                "Rudenko.ged has no family with >= 2 children — cannot spot-check closest_common_ancestor.");

            var (personA, personB, expectedParentIds) = siblingPair!.Value;

            var depthsA = BuildAncestorDepths(edges, personA);
            var depthsB = BuildAncestorDepths(edges, personB);
            var (oracleAncestorId, oracleDepthA, oracleDepthB) = FindClosestCommonAncestor(depthsA, depthsB);

            // Siblings' closest common ancestor must be one of their two known
            // parents, at depth 1 on both sides.
            Assert.Contains(oracleAncestorId, expectedParentIds);
            Assert.Equal(1, oracleDepthA);
            Assert.Equal(1, oracleDepthB);

            var ccaJson = await treeTools.GetClosestCommonAncestorAsync(personA.ToString(), personB.ToString(), tree: treeIdStr);
            using (var doc = JsonDocument.Parse(ccaJson))
            {
                var root = doc.RootElement;
                Assert.False(root.TryGetProperty("error", out _), ccaJson);

                var ancestor = root.GetProperty("ancestor");
                Assert.NotEqual(JsonValueKind.Null, ancestor.ValueKind);
                Assert.Contains(ancestor.GetProperty("ancestorId").GetGuid(), expectedParentIds);
                Assert.Equal(1, ancestor.GetProperty("maxDepth").GetInt32());
            }

            // ── Spot check 2: ancestors of "the root" (deepest ancestor chain) ──
            var rootCandidate = FindDeepestAncestorChainPerson(edges);
            var oracleRootAncestors = BuildAncestorDepths(edges, rootCandidate);
            Assert.True(oracleRootAncestors.Count > 0, "Expected the deepest-chain candidate to have at least one recorded ancestor.");

            var ancestorsJson = await treeTools.GetAncestorsAsync(rootCandidate.ToString(), maxGenerations: 50, tree: treeIdStr);
            using (var doc = JsonDocument.Parse(ancestorsJson))
            {
                var root = doc.RootElement;
                Assert.False(root.TryGetProperty("error", out _), ancestorsJson);
                Assert.Equal(oracleRootAncestors.Count, root.GetProperty("count").GetInt32());

                var toolIds = root.GetProperty("ancestors").EnumerateArray()
                    .Select(a => a.GetProperty("personId").GetGuid())
                    .ToHashSet();
                Assert.Equal(oracleRootAncestors.Keys.ToHashSet(), toolIds);

                _output.WriteLine(
                    $"Root-candidate ancestor count: {oracleRootAncestors.Count} " +
                    $"(max generation {oracleRootAncestors.Values.Max()}). Full 259-row golden diff against the " +
                    "legacy SQL Server export is a separate, manual exercise — not performed here.");
            }
        }
        finally
        {
            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    // ── independent oracle helpers (deliberately separate from McpTreeParityTests') ──

    private static async Task<List<(Guid Parent, Guid Child)>> FetchParentChildEdgesAsync(string connectionString, Guid treeId)
    {
        var edges = new List<(Guid, Guid)>();
        await using var connection = new NpgsqlConnection(connectionString);
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

    /// <summary>Finds any two children who share both parents, plus that parent pair's ids.</summary>
    private static async Task<(Guid PersonA, Guid PersonB, Guid[] ParentIds)?> FindAnySiblingPairAsync(
        string connectionString, Guid treeId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT parent_person_id, array_agg(DISTINCT child_person_id) AS children
            FROM genealogy.parent_child
            WHERE tree_id = @tree
            GROUP BY parent_person_id
            HAVING count(DISTINCT child_person_id) >= 2
            LIMIT 1;
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var parentId = reader.GetFieldValue<Guid>(0);
        var children = reader.GetFieldValue<Guid[]>(1);
        var personA = children[0];
        var personB = children[1];

        // The other parent (if any) of personA, so a two-parent family reports
        // both as acceptable closest-common-ancestor candidates.
        await using var otherParentCommand = new NpgsqlCommand(
            "SELECT DISTINCT parent_person_id FROM genealogy.parent_child WHERE tree_id = @tree AND child_person_id = @child;",
            connection);
        otherParentCommand.Parameters.Add(new NpgsqlParameter("tree", NpgsqlDbType.Uuid) { Value = treeId });
        otherParentCommand.Parameters.Add(new NpgsqlParameter("child", NpgsqlDbType.Uuid) { Value = personA });

        var parentIds = new List<Guid>();
        await using var otherReader = await otherParentCommand.ExecuteReaderAsync();
        while (await otherReader.ReadAsync())
        {
            parentIds.Add(otherReader.GetFieldValue<Guid>(0));
        }

        return (personA, personB, parentIds.ToArray());
    }

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

    private static (Guid AncestorId, int Depth1, int Depth2) FindClosestCommonAncestor(
        IReadOnlyDictionary<Guid, int> depths1, IReadOnlyDictionary<Guid, int> depths2)
    {
        var common = depths1.Keys.Intersect(depths2.Keys)
            .Select(id => (Id: id, D1: depths1[id], D2: depths2[id]))
            .OrderBy(x => Math.Max(x.D1, x.D2))
            .ThenBy(x => x.D1 + x.D2)
            .ThenBy(x => x.Id)
            .ToList();

        Assert.True(common.Count > 0, "oracle found no common ancestor between the two siblings (unexpected)");

        var best = common[0];
        return (best.Id, best.D1, best.D2);
    }

    /// <summary>
    /// Heuristic "root": the person with the largest recorded ancestor chain
    /// (breaking ties by id for determinism). A real family-tree export's main
    /// subject typically has the deepest recorded ancestry, unlike marry-in
    /// spouses or leaf descendants.
    /// </summary>
    private static Guid FindDeepestAncestorChainPerson(IReadOnlyList<(Guid Parent, Guid Child)> edges)
    {
        var allPersons = edges.Select(e => e.Parent).Concat(edges.Select(e => e.Child)).Distinct();

        var best = allPersons
            .Select(id => (Id: id, Depths: BuildAncestorDepths(edges, id)))
            .OrderByDescending(x => x.Depths.Count)
            .ThenBy(x => x.Id)
            .First();

        return best.Id;
    }
}
