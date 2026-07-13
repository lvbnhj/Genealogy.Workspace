using Genealogy.Workspace.Data.Models;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Create/list/get access to <c>genealogy.tree</c>. All orchestration lives
/// here in the .NET service layer; queries are parameterized SQL, not stored
/// procedures (see docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §3).
/// </summary>
public sealed class TreeRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public TreeRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Creates a new tree with an app-generated <see cref="Guid"/> id.
    /// </summary>
    /// <exception cref="DuplicateTreeNameException">A tree with this name already exists.</exception>
    /// <exception cref="DefaultTreeConflictException">
    /// <paramref name="isDefault"/> is true and another tree is already the default
    /// (the partial unique index on <c>is_default</c> enforces exactly one).
    /// </exception>
    public async Task<Tree> CreateTreeAsync(
        string name,
        string? description = null,
        bool isDefault = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tree name is required.", nameof(name));
        }

        const string sql = """
            INSERT INTO genealogy.tree (tree_id, name, description, is_default)
            VALUES (@tree_id, @name, @description, @is_default)
            RETURNING tree_id, name, description, root_person_id, is_default, created_at, updated_at;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
        command.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("is_default", NpgsqlDbType.Boolean) { Value = isDefault });

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Insert into genealogy.tree did not return a row.");
            }

            return MapTree(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw LooksLikeDefaultConflict(ex)
                ? new DefaultTreeConflictException()
                : new DuplicateTreeNameException(name);
        }
    }

    /// <summary>Lists every tree, ordered by name.</summary>
    public async Task<IReadOnlyList<Tree>> ListTreesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT tree_id, name, description, root_person_id, is_default, created_at, updated_at
            FROM genealogy.tree
            ORDER BY name;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<Tree>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapTree(reader));
        }

        return results;
    }

    /// <summary>Looks up a single tree by id, or null if it does not exist.</summary>
    public async Task<Tree?> GetTreeAsync(Guid treeId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT tree_id, name, description, root_person_id, is_default, created_at, updated_at
            FROM genealogy.tree
            WHERE tree_id = @tree_id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapTree(reader) : null;
    }

    private static Tree MapTree(NpgsqlDataReader reader) =>
        new(
            TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
            Name: reader.GetFieldValue<string>(reader.GetOrdinal("name")),
            Description: reader.GetNullableString("description"),
            RootPersonId: reader.GetNullableValue<Guid>("root_person_id"),
            IsDefault: reader.GetFieldValue<bool>(reader.GetOrdinal("is_default")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            UpdatedAt: reader.GetNullableValue<DateTimeOffset>("updated_at"));

    /// <summary>
    /// Distinguishes the two unique constraints an insert into
    /// <c>genealogy.tree</c> can hit: the UNIQUE <c>name</c> column and the
    /// partial unique index on <c>is_default</c>. The exact index name is
    /// assigned by migration 0002 and not part of the schema contract, so we
    /// look for "default" in the constraint name/detail rather than an exact
    /// match; any other unique violation on this insert can only be the name
    /// constraint.
    /// </summary>
    private static bool LooksLikeDefaultConflict(PostgresException ex) =>
        (ex.ConstraintName?.Contains("default", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (ex.Detail?.Contains("is_default", StringComparison.OrdinalIgnoreCase) ?? false);
}
