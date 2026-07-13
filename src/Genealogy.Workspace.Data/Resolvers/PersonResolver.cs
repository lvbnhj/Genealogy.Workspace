using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Resolvers;

/// <summary>
/// Resolves a person-scoping input (a GUID, or a name to search for) to
/// exactly one person within a given tree. Ported from the resolution logic
/// shared by the SQL Server procs <c>ged.GetPersonLifeEvents</c>,
/// <c>ged.GetPersonFamilyContext</c> and <c>ged.FindTreePerson</c>
/// (Database/Procedures/ged/*.sql): a substring match against
/// <c>genealogy.person_name.full_name_normalized</c>/<c>surname</c>, ordered
/// exact-match-first. Unlike the source (which always picks the first
/// ordered row), this resolver never silently picks among several *non-exact*
/// substring matches — it returns them as <see cref="PersonResolution.Candidates"/>
/// for the caller to disambiguate. A unique exact match is still resolved
/// automatically even when other, lower-priority substring matches exist,
/// since there is no real ambiguity in that case.
/// </summary>
public sealed class PersonResolver
{
    private const int DefaultMaxCandidates = 10;

    private readonly NpgsqlConnectionFactory _connectionFactory;

    public PersonResolver(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Resolution order: (1) if <paramref name="nameOrGuid"/> parses as a
    /// <see cref="Guid"/>, look it up directly and verify it belongs to
    /// <paramref name="treeId"/>; (2) otherwise substring-match it against
    /// names in the tree (see class remarks).
    /// </summary>
    public async Task<PersonResolution> ResolvePersonAsync(
        Guid treeId,
        string nameOrGuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrGuid))
        {
            return PersonResolution.NotFound("person name or id is required");
        }

        var trimmed = nameOrGuid.Trim();

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return Guid.TryParse(trimmed, out var personId)
            ? await ResolveByIdAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false)
            : await ResolveByNameAsync(connection, treeId, trimmed, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PersonResolution> ResolveByIdAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT person_id, primary_display_name
            FROM genealogy.person
            WHERE tree_id = @tree_id AND person_id = @person_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return PersonResolution.NotFound($"person '{personId}' not found in tree");
        }

        return PersonResolution.Resolved(
            reader.GetFieldValue<Guid>(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<string>(1));
    }

    private static async Task<PersonResolution> ResolveByNameAsync(
        NpgsqlConnection connection, Guid treeId, string name, CancellationToken cancellationToken)
    {
        var exactNormalized = name.ToLowerInvariant();
        var pattern = "%" + EscapeLikePattern(name) + "%";

        const string sql = """
            SELECT
                p.person_id,
                p.primary_display_name,
                bool_or(lower(pn.full_name_normalized) = @exact OR lower(pn.surname) = @exact) AS is_exact
            FROM genealogy.person p
            INNER JOIN genealogy.person_name pn
                ON pn.tree_id = p.tree_id
               AND pn.person_id = p.person_id
            WHERE p.tree_id = @tree_id
              AND (
                    pn.full_name_normalized ILIKE @pattern ESCAPE '\'
                 OR pn.surname ILIKE @pattern ESCAPE '\'
              )
            GROUP BY p.person_id, p.primary_display_name
            ORDER BY is_exact DESC, p.primary_display_name NULLS LAST, p.person_id
            LIMIT @limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = exactNormalized });
        command.Parameters.Add(new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = pattern });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = DefaultMaxCandidates });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var candidates = new List<(Guid PersonId, string? FullName, bool IsExact)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add((
                reader.GetFieldValue<Guid>(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<string>(1),
                reader.GetFieldValue<bool>(2)));
        }

        if (candidates.Count == 0)
        {
            return PersonResolution.NotFound($"person '{name}' not found");
        }

        if (candidates.Count == 1)
        {
            return PersonResolution.Resolved(candidates[0].PersonId, candidates[0].FullName);
        }

        var exactMatches = candidates.Where(c => c.IsExact).ToList();
        if (exactMatches.Count == 1)
        {
            return PersonResolution.Resolved(exactMatches[0].PersonId, exactMatches[0].FullName);
        }

        return PersonResolution.MultiMatch(
            candidates.Select(c => new PersonCandidate(c.PersonId, c.FullName)).ToList());
    }

    /// <summary>Escapes LIKE/ILIKE metacharacters so user input is matched literally.</summary>
    private static string EscapeLikePattern(string input) =>
        input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
}
