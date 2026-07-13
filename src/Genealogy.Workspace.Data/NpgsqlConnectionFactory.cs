using Npgsql;

namespace Genealogy.Workspace.Data;

/// <summary>
/// Builds PostgreSQL connection strings and connections from
/// <see cref="WorkspaceDbOptions"/>. This is the single place the workspace
/// speaks to PostgreSQL; there is no cross-provider abstraction.
/// </summary>
public sealed class NpgsqlConnectionFactory
{
    private readonly WorkspaceDbOptions _options;

    public NpgsqlConnectionFactory(WorkspaceDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>The composed connection string for the configured workspace.</summary>
    public string ConnectionString => BuildConnectionString(_options);

    /// <summary>Creates a new, unopened <see cref="NpgsqlConnection"/>.</summary>
    public NpgsqlConnection Create() => new(ConnectionString);

    /// <summary>Composes an Npgsql connection string from the given options.</summary>
    public static string BuildConnectionString(WorkspaceDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            Password = options.Password,
        };

        return builder.ConnectionString;
    }
}
