using Genealogy.Workspace.Data;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 2 exit criterion: "no vector/embedding or DNA columns exist."
/// Migration 0002_genealogy_core_schema.sql deliberately drops every
/// vector/embedding column that existed on the SQL Server <c>ged.*</c> source
/// tables (NameEmbedding, PlaceEmbedding, ...) and never introduces DNA
/// columns. These tests query <c>information_schema.columns</c> directly to
/// prove that shape, independent of any repository code.
/// </summary>
public sealed class SchemaShapeTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    public SchemaShapeTests(WorkspaceEnvironmentFixture fixture)
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
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task GenealogySchema_HasNoVectorTypedColumns()
    {
        const string sql = """
            SELECT table_name, column_name, data_type, udt_name
            FROM information_schema.columns
            WHERE table_schema = 'genealogy'
              AND (data_type ILIKE 'vector' OR udt_name ILIKE 'vector');
            """;

        var offenders = await QueryColumnDescriptionsAsync(sql);

        Assert.True(
            offenders.Count == 0,
            $"Found vector-typed column(s) in genealogy schema: {string.Join(", ", offenders)}");
    }

    [Fact]
    public async Task GenealogySchema_HasNoEmbeddingNamedColumns()
    {
        const string sql = """
            SELECT table_name, column_name, data_type, udt_name
            FROM information_schema.columns
            WHERE table_schema = 'genealogy'
              AND column_name ILIKE '%embedding%';
            """;

        var offenders = await QueryColumnDescriptionsAsync(sql);

        Assert.True(
            offenders.Count == 0,
            $"Found embedding-named column(s) in genealogy schema: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("place")]
    [InlineData("name_variant_rule")]
    public async Task GlobalTable_HasNoTreeIdColumn(string tableName)
    {
        const string sql = """
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema = 'genealogy'
              AND table_name = @table_name
              AND column_name = 'tree_id';
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("table_name", tableName);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("place")]
    [InlineData("name_variant_rule")]
    public async Task GlobalTable_ActuallyExistsWithColumns(string tableName)
    {
        // Guards against the two assertions above passing vacuously if the
        // table name were ever misspelled or the table didn't exist.
        const string sql = """
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema = 'genealogy'
              AND table_name = @table_name;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("table_name", tableName);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

        Assert.True(count > 0, $"Expected genealogy.{tableName} to exist with columns.");
    }

    private async Task<List<string>> QueryColumnDescriptionsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var descriptions = new List<string>();
        while (await reader.ReadAsync())
        {
            descriptions.Add(
                $"{reader.GetString(0)}.{reader.GetString(1)} ({reader.GetString(2)}/{reader.GetString(3)})");
        }

        return descriptions;
    }
}
