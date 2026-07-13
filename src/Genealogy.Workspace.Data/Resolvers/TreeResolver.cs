using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Resolvers;

/// <summary>
/// Resolves a tree-scoping input (an explicit name or GUID, or nothing at
/// all) to exactly one <c>genealogy.tree</c> row. Every tree-query repository
/// in this project is scoped to a single tree; this resolver is the one place
/// that turns ambiguous or absent user input into that scope, per the Phase 5
/// tree-scoping decision (docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10):
/// a name matching multiple or zero trees, or no default tree when none was
/// specified, is surfaced as an explicit <see cref="TreeResolution"/> —
/// never a silent pick.
/// </summary>
public sealed class TreeResolver
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public TreeResolver(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Resolution order: (1) if <paramref name="nameOrGuid"/> parses as a
    /// <see cref="Guid"/>, look up that tree id directly; (2) otherwise match
    /// it case-insensitively against every tree name — <c>genealogy.tree.name</c>
    /// is unique only case-sensitively, so two differently-cased trees can
    /// both match here, which is reported as a "multiple trees" failure
    /// rather than picked arbitrarily; (3) if <paramref name="nameOrGuid"/> is
    /// null/blank, fall back to the tree flagged <c>is_default</c>.
    /// </summary>
    public async Task<TreeResolution> ResolveTreeAsync(
        string? nameOrGuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(nameOrGuid))
        {
            return await ResolveDefaultAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        var trimmed = nameOrGuid.Trim();

        return Guid.TryParse(trimmed, out var treeId)
            ? await ResolveByIdAsync(connection, treeId, cancellationToken).ConfigureAwait(false)
            : await ResolveByNameAsync(connection, trimmed, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TreeResolution> ResolveByIdAsync(
        NpgsqlConnection connection, Guid treeId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tree_id, name, is_default
            FROM genealogy.tree
            WHERE tree_id = @tree_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return TreeResolution.NotResolved($"tree '{treeId}' not found");
        }

        return MapRow(reader);
    }

    private static async Task<TreeResolution> ResolveByNameAsync(
        NpgsqlConnection connection, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tree_id, name, is_default
            FROM genealogy.tree
            WHERE lower(name) = lower(@name)
            ORDER BY name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        TreeResolution? firstMatch = null;
        var matchCount = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            matchCount++;
            firstMatch ??= MapRow(reader);
        }

        return matchCount switch
        {
            0 => TreeResolution.NotResolved($"tree '{name}' not found"),
            1 => firstMatch!,
            _ => TreeResolution.NotResolved($"multiple trees named '{name}'"),
        };
    }

    private static async Task<TreeResolution> ResolveDefaultAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tree_id, name, is_default
            FROM genealogy.tree
            WHERE is_default
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapRow(reader)
            : TreeResolution.NotResolved("no default tree");
    }

    private static TreeResolution MapRow(NpgsqlDataReader reader) =>
        TreeResolution.Resolved(
            reader.GetFieldValue<Guid>(0),
            reader.GetFieldValue<string>(1),
            reader.GetFieldValue<bool>(2));
}
