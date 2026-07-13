using System.Security.Cryptography;
using Genealogy.Workspace.Data;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 1 exit criterion: "tests create an isolated database and tear it
/// down." Each test method gets its own uniquely named database (created in
/// <see cref="InitializeAsync"/> and force-dropped in
/// <see cref="DisposeAsync"/>, which xunit runs even when the test fails),
/// so tests never collide with each other or with a real workspace database.
/// </summary>
public sealed class DatabaseLifecycleTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    public DatabaseLifecycleTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = "gw_test_" + RandomHex(12);
        await _fixture.CreateDatabaseAsync(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(
            _fixture.BuildOptionsForDatabase(_databaseName));
    }

    public async Task DisposeAsync()
    {
        // Force-drop even if an assertion above threw: xunit always invokes
        // IAsyncLifetime.DisposeAsync after the test body, success or failure.
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task Migrate_AppliesInitialScript_CreatesSchemas_AndJournalsIt()
    {
        var engine = new MigrationEngine(_connectionString);

        var applied = engine.Migrate();

        Assert.Contains(applied, name => name.Contains("0001_create_schemas", StringComparison.Ordinal));

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        Assert.True(await SchemaExistsAsync(connection, "genealogy"));
        Assert.True(await SchemaExistsAsync(connection, "research"));

        var journaledScripts = await GetJournaledScriptNamesAsync(connection);
        Assert.Contains(journaledScripts, name => name.Contains("0001_create_schemas", StringComparison.Ordinal));
    }

    [Fact]
    public void Migrate_SecondRunOnSameDatabase_AppliesNothing_AndStatusReflectsAllAppliedZeroPending()
    {
        var engine = new MigrationEngine(_connectionString);

        var firstRun = engine.Migrate();
        Assert.NotEmpty(firstRun);

        var secondRun = engine.Migrate();
        Assert.Empty(secondRun);

        var status = engine.Status();
        // Every migration in the repository is applied and nothing is pending.
        // Assert against the first run's count rather than a hard-coded number
        // so the test survives new migrations being added.
        Assert.Equal(firstRun.Count, status.Applied.Count);
        Assert.NotEmpty(status.Applied);
        Assert.Empty(status.Pending);
    }

    private static async Task<bool> SchemaExistsAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from information_schema.schemata where schema_name = @schema;";
        command.Parameters.AddWithValue("schema", schemaName);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        return count > 0;
    }

    private static async Task<List<string>> GetJournaledScriptNamesAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select scriptname from public.schema_version;";
        await using var reader = await command.ExecuteReaderAsync();

        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string RandomHex(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes((length + 1) / 2);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }
}
