using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Traversal;
using Genealogy.Workspace.McpServer.Tools;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 5 exit criterion: the new standalone <c>Genealogy.Workspace.McpServer</c>
/// hosts at least one working tool wired to the real workspace data layer.
/// This test constructs <see cref="TreeTools"/> directly (bypassing the MCP
/// stdio host, which xunit cannot easily drive) against a fresh, migrated
/// database seeded with two <c>genealogy.tree</c> rows, and asserts that
/// <c>list_tree_datasets</c> returns both of them.
/// </summary>
public sealed class McpServerBootTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    public McpServerBootTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(
            _fixture.BuildOptionsForDatabase(_databaseName));

        var engine = new MigrationEngine(_connectionString);
        engine.Migrate();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task ListTreeDatasets_ReturnsSeededTrees()
    {
        var options = _fixture.BuildOptionsForDatabase(_databaseName);
        var connectionFactory = new NpgsqlConnectionFactory(options);

        await using (var seedConnection = new NpgsqlConnection(_connectionString))
        {
            await seedConnection.OpenAsync();
            await TestSeeding.InsertTreeAsync(seedConnection, Guid.NewGuid(), "Paternal line", isDefault: true);
            await TestSeeding.InsertTreeAsync(seedConnection, Guid.NewGuid(), "Maternal line");
        }

        var tools = new TreeTools(
            new TreeRepository(connectionFactory),
            new PersonRepository(connectionFactory),
            new PersonSearchRepository(connectionFactory),
            new RichFamilyContextRepository(connectionFactory),
            new PersonEventsRepository(connectionFactory),
            new TreeTraversalRepository(connectionFactory),
            new TreeResolver(connectionFactory),
            new PersonResolver(connectionFactory));

        var json = await tools.ListTreeDatasetsAsync();

        Assert.Contains("Paternal line", json, StringComparison.Ordinal);
        Assert.Contains("Maternal line", json, StringComparison.Ordinal);
    }
}
