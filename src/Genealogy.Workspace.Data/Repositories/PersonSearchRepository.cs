using Genealogy.Workspace.Data.Models;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Multi-constraint person search within one tree. Ported from the SQL
/// Server proc <c>ged.FindTreePerson</c>
/// (Database/Procedures/ged/FindTreePerson.sql): required name substring
/// match plus optional father/mother/child/spouse/place/year-range filters,
/// each expressed as <c>(@param IS NULL OR EXISTS (...))</c> so an absent
/// filter never restricts the result set. No DNA columns. Parameterized SQL
/// only — see docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §3.
/// </summary>
public sealed class PersonSearchRepository
{
    private const int DefaultMaxResults = 20;

    private readonly NpgsqlConnectionFactory _connectionFactory;

    public PersonSearchRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<TreePersonSearchResult>> FindTreePersonAsync(
        Guid treeId,
        string name,
        string? father = null,
        string? mother = null,
        string? child = null,
        string? spouse = null,
        string? place = null,
        int? yearFrom = null,
        int? yearTo = null,
        int maxResults = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        var effectiveMaxResults = Math.Clamp(maxResults, 1, 500);

        const string sql = """
            SELECT
                p.person_id,
                p.primary_display_name AS full_name,
                p.sex,
                p.is_living,
                birt.birth_year,
                birt.birth_place,
                deat.death_year
            FROM genealogy.person p

            LEFT JOIN LATERAL (
                SELECT
                    coalesce(e.year_from, e.year_to) AS birth_year,
                    pl.place_normalized               AS birth_place
                FROM genealogy.event e
                LEFT JOIN genealogy.place pl ON pl.place_id = e.place_id
                WHERE e.tree_id = p.tree_id AND e.person_id = p.person_id AND e.event_type = 'BIRT'
                ORDER BY e.year_from
                LIMIT 1
            ) birt ON true

            LEFT JOIN LATERAL (
                SELECT coalesce(e.year_from, e.year_to) AS death_year
                FROM genealogy.event e
                WHERE e.tree_id = p.tree_id AND e.person_id = p.person_id AND e.event_type = 'DEAT'
                ORDER BY e.year_from
                LIMIT 1
            ) deat ON true

            WHERE p.tree_id = @tree_id

              -- Name filter (required)
              AND EXISTS (
                  SELECT 1 FROM genealogy.person_name pn
                  WHERE pn.tree_id = p.tree_id AND pn.person_id = p.person_id
                    AND (pn.full_name_normalized ILIKE @name_pattern ESCAPE '\'
                         OR pn.surname ILIKE @name_pattern ESCAPE '\')
              )

              -- Father filter
              AND (@father_pattern IS NULL OR EXISTS (
                  SELECT 1
                  FROM genealogy.parent_child pc
                  JOIN genealogy.person fp ON fp.tree_id = pc.tree_id AND fp.person_id = pc.parent_person_id
                  JOIN genealogy.person_name fpn ON fpn.tree_id = pc.tree_id AND fpn.person_id = pc.parent_person_id
                  WHERE pc.tree_id = p.tree_id AND pc.child_person_id = p.person_id
                    AND fp.sex = 'M'
                    AND (fpn.full_name_normalized ILIKE @father_pattern ESCAPE '\'
                         OR fpn.surname ILIKE @father_pattern ESCAPE '\')
              ))

              -- Mother filter
              AND (@mother_pattern IS NULL OR EXISTS (
                  SELECT 1
                  FROM genealogy.parent_child pc
                  JOIN genealogy.person mp ON mp.tree_id = pc.tree_id AND mp.person_id = pc.parent_person_id
                  JOIN genealogy.person_name mpn ON mpn.tree_id = pc.tree_id AND mpn.person_id = pc.parent_person_id
                  WHERE pc.tree_id = p.tree_id AND pc.child_person_id = p.person_id
                    AND mp.sex = 'F'
                    AND (mpn.full_name_normalized ILIKE @mother_pattern ESCAPE '\'
                         OR mpn.surname ILIKE @mother_pattern ESCAPE '\')
              ))

              -- Child filter
              AND (@child_pattern IS NULL OR EXISTS (
                  SELECT 1
                  FROM genealogy.parent_child pc
                  JOIN genealogy.person_name cpn ON cpn.tree_id = pc.tree_id AND cpn.person_id = pc.child_person_id
                  WHERE pc.tree_id = p.tree_id AND pc.parent_person_id = p.person_id
                    AND (cpn.full_name_normalized ILIKE @child_pattern ESCAPE '\'
                         OR cpn.surname ILIKE @child_pattern ESCAPE '\')
              ))

              -- Spouse filter
              AND (@spouse_pattern IS NULL OR EXISTS (
                  SELECT 1
                  FROM genealogy.family f
                  JOIN genealogy.person_name spn
                      ON spn.tree_id = f.tree_id
                     AND spn.person_id = CASE WHEN f.spouse1_person_id = p.person_id
                                               THEN f.spouse2_person_id
                                               ELSE f.spouse1_person_id
                                          END
                  WHERE f.tree_id = p.tree_id
                    AND (f.spouse1_person_id = p.person_id OR f.spouse2_person_id = p.person_id)
                    AND (spn.full_name_normalized ILIKE @spouse_pattern ESCAPE '\'
                         OR spn.surname ILIKE @spouse_pattern ESCAPE '\')
              ))

              -- Place filter (any event)
              AND (@place_pattern IS NULL OR EXISTS (
                  SELECT 1
                  FROM genealogy.event e
                  JOIN genealogy.place pl ON pl.place_id = e.place_id
                  WHERE e.tree_id = p.tree_id AND e.person_id = p.person_id
                    AND (pl.place_normalized ILIKE @place_pattern ESCAPE '\'
                         OR pl.place_raw ILIKE @place_pattern ESCAPE '\')
              ))

              -- Birth year lower bound
              AND (@year_from IS NULL OR EXISTS (
                  SELECT 1 FROM genealogy.event e
                  WHERE e.tree_id = p.tree_id AND e.person_id = p.person_id AND e.event_type = 'BIRT'
                    AND coalesce(e.year_from, e.year_to) >= @year_from
              ))

              -- Birth year upper bound
              AND (@year_to IS NULL OR EXISTS (
                  SELECT 1 FROM genealogy.event e
                  WHERE e.tree_id = p.tree_id AND e.person_id = p.person_id AND e.event_type = 'BIRT'
                    AND coalesce(e.year_from, e.year_to) <= @year_to
              ))

            ORDER BY p.primary_display_name NULLS LAST, p.person_id
            LIMIT @max_results;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("name_pattern", NpgsqlDbType.Text) { Value = ToPattern(name)! });
        command.Parameters.Add(new NpgsqlParameter("father_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(father) });
        command.Parameters.Add(new NpgsqlParameter("mother_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(mother) });
        command.Parameters.Add(new NpgsqlParameter("child_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(child) });
        command.Parameters.Add(new NpgsqlParameter("spouse_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(spouse) });
        command.Parameters.Add(new NpgsqlParameter("place_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(place) });
        command.Parameters.Add(new NpgsqlParameter("year_from", NpgsqlDbType.Integer) { Value = (object?)yearFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("year_to", NpgsqlDbType.Integer) { Value = (object?)yearTo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("max_results", NpgsqlDbType.Integer) { Value = effectiveMaxResults });

        var results = new List<TreePersonSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new TreePersonSearchResult(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                FullName: reader.GetNullableString("full_name"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                BirthYear: ReadCoalescedYear(reader, "birth_year"),
                BirthPlace: reader.GetNullableString("birth_place"),
                DeathYear: ReadCoalescedYear(reader, "death_year")));
        }

        return results;
    }

    /// <summary>
    /// <c>coalesce(e.year_from, e.year_to)</c> is a <c>smallint</c> expression
    /// in PostgreSQL, so Npgsql reports its column type as <c>int2</c> exactly
    /// like a plain <c>smallint</c> column.
    /// </summary>
    private static short? ReadCoalescedYear(NpgsqlDataReader reader, string column) =>
        reader.GetNullableValue<short>(column);

    private static object ToPatternOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : ToPattern(value)!;

    private static string? ToPattern(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : "%" + EscapeLikePattern(value.Trim()) + "%";

    /// <summary>Escapes LIKE/ILIKE metacharacters so user input is matched literally.</summary>
    private static string EscapeLikePattern(string input) =>
        input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
}
