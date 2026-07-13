using Genealogy.Workspace.Data.Models;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Reads the immediate family context (parents, children, spouses) of a
/// person within a tree. Parents/children come from
/// <c>genealogy.parent_child</c>; spouses are derived from
/// <c>genealogy.family</c> since there is no dedicated spouse-edge table.
/// Parameterized SQL only — see docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §3.
/// </summary>
public sealed class FamilyContextRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public FamilyContextRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Builds the family context for a person. Every list is empty (never
    /// null) when the person has no relatives of that kind.
    /// </summary>
    public async Task<FamilyContext> GetFamilyContextAsync(
        Guid treeId,
        Guid personId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var parents = await GetParentsAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);
        var children = await GetChildrenAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);
        var spouses = await GetSpousesAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);

        return new FamilyContext(treeId, personId, parents, children, spouses);
    }

    private static Task<IReadOnlyList<FamilyMember>> GetParentsAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.person_id, p.primary_display_name, p.surname_normalized, p.sex, p.is_living, pc.relation_type
            FROM genealogy.parent_child pc
            INNER JOIN genealogy.person p
                ON p.tree_id = pc.tree_id
               AND p.person_id = pc.parent_person_id
            WHERE pc.tree_id = @tree_id
              AND pc.child_person_id = @person_id
            ORDER BY p.primary_display_name NULLS LAST, p.person_id;
            """;

        return QueryFamilyMembersAsync(connection, sql, treeId, personId, cancellationToken);
    }

    private static Task<IReadOnlyList<FamilyMember>> GetChildrenAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.person_id, p.primary_display_name, p.surname_normalized, p.sex, p.is_living, pc.relation_type
            FROM genealogy.parent_child pc
            INNER JOIN genealogy.person p
                ON p.tree_id = pc.tree_id
               AND p.person_id = pc.child_person_id
            WHERE pc.tree_id = @tree_id
              AND pc.parent_person_id = @person_id
            ORDER BY p.primary_display_name NULLS LAST, p.person_id;
            """;

        return QueryFamilyMembersAsync(connection, sql, treeId, personId, cancellationToken);
    }

    private static async Task<IReadOnlyList<FamilyMember>> QueryFamilyMembersAsync(
        NpgsqlConnection connection, string sql, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<FamilyMember>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new FamilyMember(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
                SurnameNormalized: reader.GetNullableString("surname_normalized"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                RelationType: reader.GetFieldValue<string>(reader.GetOrdinal("relation_type"))));
        }

        return results;
    }

    /// <summary>
    /// Spouses are not stored directly: <c>genealogy.family</c> holds an
    /// unordered pair (<c>spouse1_person_id</c>, <c>spouse2_person_id</c>). We
    /// select every family row where the requested person is on either side,
    /// then project the *other* side as the spouse via a CASE expression, and
    /// join back to <c>person</c> on that same expression for names.
    /// </summary>
    private static async Task<IReadOnlyList<Spouse>> GetSpousesAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                f.family_id,
                CASE WHEN f.spouse1_person_id = @person_id
                     THEN f.spouse2_person_id
                     ELSE f.spouse1_person_id
                END AS other_spouse_person_id,
                p.primary_display_name, p.surname_normalized, p.sex, p.is_living,
                f.marriage_year, f.marriage_place_raw, f.marriage_date_raw
            FROM genealogy.family f
            INNER JOIN genealogy.person p
                ON p.tree_id = f.tree_id
               AND p.person_id = CASE WHEN f.spouse1_person_id = @person_id
                                       THEN f.spouse2_person_id
                                       ELSE f.spouse1_person_id
                                  END
            WHERE f.tree_id = @tree_id
              AND (f.spouse1_person_id = @person_id OR f.spouse2_person_id = @person_id)
            ORDER BY p.primary_display_name NULLS LAST, p.person_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<Spouse>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new Spouse(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("other_spouse_person_id")),
                PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
                SurnameNormalized: reader.GetNullableString("surname_normalized"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                FamilyId: reader.GetFieldValue<Guid>(reader.GetOrdinal("family_id")),
                MarriageYear: reader.GetNullableValue<short>("marriage_year"),
                MarriagePlaceRaw: reader.GetNullableString("marriage_place_raw"),
                MarriageDateRaw: reader.GetNullableString("marriage_date_raw")));
        }

        return results;
    }
}
