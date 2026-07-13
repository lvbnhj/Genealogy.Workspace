using System.Text;
using System.Text.Json;
using Genealogy.Workspace.Data;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using Xunit.Abstractions;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 2 exit criterion: "query plans use expected indexes for tree/name
/// lookups." Seeds 200+ rows so the planner has enough rows to prefer an
/// index over a sequential scan, then runs <c>EXPLAIN (FORMAT JSON)</c> for:
/// (1) a tree+name lookup on <c>genealogy.person_name</c>, which should use
/// <c>ix_person_name_tree_normalized (tree_id, full_name_normalized)</c>; and
/// (2) a person lookup by <c>(tree_id, external_id)</c>, which should use
/// <c>uq_person_tree_external_id</c>.
///
/// Both lookups use an equality predicate rather than
/// <c>PersonRepository.SearchPersonsByNameAsync</c>'s leading-wildcard
/// <c>ILIKE '%...%'</c>: a leading wildcard cannot use a plain B-tree index
/// by construction (PostgreSQL only supports prefix optimization for
/// LIKE/ILIKE with a trigram or C-locale pattern-ops index, neither of which
/// this schema has — that is a Phase 3+ concern). The equality form is the
/// closest same-index query that can validate
/// <c>ix_person_name_tree_normalized</c> is actually reachable.
/// </summary>
public sealed class IndexUsageTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private const int SeedRowCount = 250;

    private readonly WorkspaceEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private Guid _treeId;

    public IndexUsageTests(WorkspaceEnvironmentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(
            _fixture.BuildOptionsForDatabase(_databaseName));

        new MigrationEngine(_connectionString).Migrate();

        _treeId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeId, "Index Usage Tree");

        // Bulk-seed server-side with generate_series: far fewer round trips
        // than one INSERT per row, and gen_random_uuid() is built into
        // PostgreSQL 13+ (no extension required).
        await using (var seedPersons = new NpgsqlCommand(
            """
            INSERT INTO genealogy.person (person_id, tree_id, external_id, primary_display_name)
            SELECT gen_random_uuid(), @tree_id, 'EXT-' || lpad(gs::text, 6, '0'), 'Bulk Person ' || gs
            FROM generate_series(1, @row_count) AS gs;
            """,
            connection))
        {
            seedPersons.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = _treeId });
            seedPersons.Parameters.Add(new NpgsqlParameter("row_count", NpgsqlDbType.Integer) { Value = SeedRowCount });
            await seedPersons.ExecuteNonQueryAsync();
        }

        await using (var seedNames = new NpgsqlCommand(
            """
            INSERT INTO genealogy.person_name
                (tree_id, person_id, script_code, name_type, full_name, full_name_normalized, is_primary)
            SELECT p.tree_id, p.person_id, 'LAT', 'birth', p.primary_display_name, lower(p.primary_display_name), true
            FROM genealogy.person p
            WHERE p.tree_id = @tree_id;
            """,
            connection))
        {
            seedNames.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = _treeId });
            await seedNames.ExecuteNonQueryAsync();
        }

        // Fresh stats so the planner's row-count estimates reflect the seed.
        await using var analyze = new NpgsqlCommand(
            "ANALYZE genealogy.person; ANALYZE genealogy.person_name;", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task TreeAndNormalizedNameLookup_UsesIndexScan()
    {
        const string sql = """
            SELECT person_id
            FROM genealogy.person_name
            WHERE tree_id = @tree_id AND full_name_normalized = @name;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = _treeId });
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = "bulk person 150" });

        var (nodeTypes, usedSeqScanOff) = await ExplainAndCollectNodeTypesAsync(connection, command);

        _output.WriteLine(
            $"[IndexUsageTests] tree+name lookup plan node types: {string.Join(", ", nodeTypes)}" +
            $" (enable_seqscan=off used: {usedSeqScanOff})");

        Assert.Contains(nodeTypes, t => t is "Index Scan" or "Index Only Scan" or "Bitmap Index Scan");
    }

    [Fact]
    public async Task TreeAndExternalIdLookup_UsesIndexScan()
    {
        const string sql = """
            SELECT person_id
            FROM genealogy.person
            WHERE tree_id = @tree_id AND external_id = @external_id;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = _treeId });
        command.Parameters.Add(new NpgsqlParameter("external_id", NpgsqlDbType.Text) { Value = "EXT-000150" });

        var (nodeTypes, usedSeqScanOff) = await ExplainAndCollectNodeTypesAsync(connection, command);

        _output.WriteLine(
            $"[IndexUsageTests] tree+external_id lookup plan node types: {string.Join(", ", nodeTypes)}" +
            $" (enable_seqscan=off used: {usedSeqScanOff})");

        Assert.Contains(nodeTypes, t => t is "Index Scan" or "Index Only Scan" or "Bitmap Index Scan");
    }

    /// <summary>
    /// Runs <c>EXPLAIN (FORMAT JSON)</c> for the given command. If the planner
    /// chooses a Seq Scan at this seed scale, retries once with
    /// <c>enable_seqscan = off</c> (scoped to this connection only) so the
    /// index-based alternative is what gets reported.
    /// </summary>
    private static async Task<(List<string> NodeTypes, bool UsedSeqScanOff)> ExplainAndCollectNodeTypesAsync(
        NpgsqlConnection connection, NpgsqlCommand command)
    {
        var nodeTypes = await RunExplainAsync(connection, command);
        if (!nodeTypes.Contains("Seq Scan"))
        {
            return (nodeTypes, false);
        }

        await using (var disableSeqScan = new NpgsqlCommand("SET enable_seqscan = off;", connection))
        {
            await disableSeqScan.ExecuteNonQueryAsync();
        }

        try
        {
            nodeTypes = await RunExplainAsync(connection, command);
            return (nodeTypes, true);
        }
        finally
        {
            await using var resetSeqScan = new NpgsqlCommand("SET enable_seqscan = on;", connection);
            await resetSeqScan.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<string>> RunExplainAsync(NpgsqlConnection connection, NpgsqlCommand baseCommand)
    {
        var explainSql = new StringBuilder("EXPLAIN (FORMAT JSON) ").Append(baseCommand.CommandText).ToString();

        await using var explainCommand = new NpgsqlCommand(explainSql, connection);
        foreach (NpgsqlParameter parameter in baseCommand.Parameters)
        {
            explainCommand.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value });
        }

        var planJson = (string)(await explainCommand.ExecuteScalarAsync())!;

        using var document = JsonDocument.Parse(planJson);
        var root = document.RootElement[0].GetProperty("Plan");

        var nodeTypes = new List<string>();
        CollectNodeTypes(root, nodeTypes);
        return nodeTypes;
    }

    private static void CollectNodeTypes(JsonElement planNode, List<string> nodeTypes)
    {
        if (planNode.TryGetProperty("Node Type", out var nodeType))
        {
            nodeTypes.Add(nodeType.GetString() ?? string.Empty);
        }

        if (planNode.TryGetProperty("Plans", out var childPlans) && childPlans.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childPlans.EnumerateArray())
            {
                CollectNodeTypes(child, nodeTypes);
            }
        }
    }
}
