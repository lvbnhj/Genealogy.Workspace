using Genealogy.Workspace.Data.Models;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Get/search access to <c>genealogy.person</c> and <c>genealogy.person_name</c>,
/// always scoped by tree. Parameterized SQL only — see
/// docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §3.
/// </summary>
public sealed class PersonRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public PersonRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Looks up a single person by (tree, person) id, including their primary
    /// display name row from <c>genealogy.person_name</c> when one exists.
    /// </summary>
    public async Task<Person?> GetPersonAsync(Guid treeId, Guid personId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                p.person_id, p.tree_id, p.external_id, p.sex, p.is_living,
                p.primary_display_name, p.surname_normalized, p.created_at, p.updated_at,
                pn.person_name_id, pn.script_code, pn.name_type, pn.given, pn.surname,
                pn.full_name, pn.full_name_normalized, pn.is_primary,
                pn.created_at AS name_created_at
            FROM genealogy.person p
            LEFT JOIN genealogy.person_name pn
                ON pn.tree_id = p.tree_id
               AND pn.person_id = p.person_id
               AND pn.is_primary = true
            WHERE p.tree_id = @tree_id
              AND p.person_id = @person_id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapPersonWithPrimaryName(reader) : null;
    }

    /// <summary>
    /// Searches persons in a tree by name. Matches case-insensitively against
    /// both <c>person_name.full_name_normalized</c> and <c>person_name.surname</c>
    /// as a substring (works for both Cyrillic and Latin text, since it is a
    /// plain ILIKE comparison against whatever is already stored — no
    /// transliteration/normalization is performed here; that is Phase 3).
    /// </summary>
    public async Task<IReadOnlyList<Person>> SearchPersonsByNameAsync(
        Guid treeId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<Person>();
        }

        var effectiveLimit = Math.Clamp(limit, 1, 500);
        var pattern = "%" + EscapeLikePattern(query.Trim()) + "%";

        const string sql = """
            SELECT DISTINCT
                p.person_id, p.tree_id, p.external_id, p.sex, p.is_living,
                p.primary_display_name, p.surname_normalized, p.created_at, p.updated_at
            FROM genealogy.person p
            INNER JOIN genealogy.person_name pn
                ON pn.tree_id = p.tree_id
               AND pn.person_id = p.person_id
            WHERE p.tree_id = @tree_id
              AND (
                    pn.full_name_normalized ILIKE @pattern ESCAPE '\'
                 OR pn.surname ILIKE @pattern ESCAPE '\'
              )
            ORDER BY p.primary_display_name NULLS LAST, p.person_id
            LIMIT @limit;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = pattern });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = effectiveLimit });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<Person>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapPersonWithoutName(reader));
        }

        return results;
    }

    /// <summary>Escapes LIKE/ILIKE metacharacters so user input is matched literally.</summary>
    private static string EscapeLikePattern(string input) =>
        input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    private static Person MapPersonWithoutName(NpgsqlDataReader reader) => MapPersonCore(reader, primaryName: null);

    private static Person MapPersonWithPrimaryName(NpgsqlDataReader reader)
    {
        var personNameIdOrdinal = reader.GetOrdinal("person_name_id");
        PersonName? primaryName = null;

        if (!reader.IsDBNull(personNameIdOrdinal))
        {
            primaryName = new PersonName(
                PersonNameId: reader.GetFieldValue<long>(personNameIdOrdinal),
                TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                ScriptCode: reader.GetFieldValue<string>(reader.GetOrdinal("script_code")),
                NameType: reader.GetFieldValue<string>(reader.GetOrdinal("name_type")),
                Given: reader.GetNullableString("given"),
                Surname: reader.GetNullableString("surname"),
                FullName: reader.GetFieldValue<string>(reader.GetOrdinal("full_name")),
                FullNameNormalized: reader.GetFieldValue<string>(reader.GetOrdinal("full_name_normalized")),
                IsPrimary: reader.GetFieldValue<bool>(reader.GetOrdinal("is_primary")),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("name_created_at")));
        }

        return MapPersonCore(reader, primaryName);
    }

    private static Person MapPersonCore(NpgsqlDataReader reader, PersonName? primaryName) =>
        new(
            PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
            TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
            ExternalId: reader.GetNullableString("external_id"),
            Sex: reader.GetNullableChar("sex"),
            IsLiving: reader.GetNullableValue<bool>("is_living"),
            PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
            SurnameNormalized: reader.GetNullableString("surname_normalized"),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            UpdatedAt: reader.GetNullableValue<DateTimeOffset>("updated_at"),
            PrimaryName: primaryName);
}
