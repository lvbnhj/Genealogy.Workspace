using Genealogy.Workspace.Data.Models;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Reads the full family context for a person, at parity with the SQL Server
/// proc <c>ged.GetPersonFamilyContext</c>
/// (Database/Procedures/ged/GetPersonFamilyContext.sql): life events, parents,
/// siblings, marriages and children, each enriched with birth/death
/// year+place where the source provides it. This is a new, richer sibling of
/// the Phase 2 <see cref="FamilyContextRepository"/> (which stays as-is, at
/// name+sex+living granularity, for its existing callers/tests) rather than
/// an in-place extension. Parameterized SQL only — see
/// docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §3.
/// </summary>
public sealed class RichFamilyContextRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public RichFamilyContextRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>Returns null when the person does not exist in the given tree.</summary>
    public async Task<RichFamilyContext?> GetRichFamilyContextAsync(
        Guid treeId,
        Guid personId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Reuses PersonEventsRepository's internal query helpers against this
        // same connection, rather than duplicating the event query or opening
        // a second connection via PersonEventsRepository's public API.
        var header = await PersonEventsRepository.ReadHeaderAsync(connection, treeId, personId, cancellationToken)
            .ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var lifeEvents = await PersonEventsRepository.ReadEventsAsync(connection, treeId, personId, cancellationToken)
            .ConfigureAwait(false);
        var parents = await GetParentsAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);
        var siblings = await GetSiblingsAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);
        var marriages = await GetMarriagesAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);
        var children = await GetChildrenAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);

        return new RichFamilyContext(
            TreeId: treeId,
            PersonId: personId,
            PrimaryDisplayName: header.FullName,
            Sex: header.Sex,
            IsLiving: header.IsLiving,
            LifeEvents: lifeEvents,
            Parents: parents,
            Siblings: siblings,
            Marriages: marriages,
            Children: children);
    }

    // ------------------------------------------------------------------
    // Parents (mirrors ged.GetPersonFamilyContext result set 2)
    // ------------------------------------------------------------------
    private static async Task<IReadOnlyList<RichFamilyMember>> GetParentsAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                parent.person_id,
                parent.primary_display_name,
                parent.sex,
                parent.is_living,
                birt.year_from  AS birth_year,
                birt_place.place_raw AS birth_place,
                deat.year_from  AS death_year,
                deat_place.place_raw AS death_place
            FROM genealogy.parent_child pc
            JOIN genealogy.person parent
                ON parent.tree_id = pc.tree_id AND parent.person_id = pc.parent_person_id
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = pc.tree_id AND e.person_id = parent.person_id AND e.event_type = 'BIRT'
                ORDER BY e.year_from
                LIMIT 1
            ) birt ON true
            LEFT JOIN genealogy.place birt_place ON birt_place.place_id = birt.place_id
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = pc.tree_id AND e.person_id = parent.person_id AND e.event_type = 'DEAT'
                ORDER BY e.year_from
                LIMIT 1
            ) deat ON true
            LEFT JOIN genealogy.place deat_place ON deat_place.place_id = deat.place_id
            WHERE pc.tree_id = @tree_id
              AND pc.child_person_id = @person_id
            ORDER BY parent.sex DESC NULLS LAST, birt.year_from;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddTreeAndPerson(command, treeId, personId);

        var results = new List<RichFamilyMember>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RichFamilyMember(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                BirthYear: reader.GetNullableValue<short>("birth_year"),
                BirthPlace: reader.GetNullableString("birth_place"),
                DeathYear: reader.GetNullableValue<short>("death_year"),
                DeathPlace: reader.GetNullableString("death_place")));
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Siblings (mirrors ged.GetPersonFamilyContext result set 3)
    // ------------------------------------------------------------------
    private static async Task<IReadOnlyList<RichSibling>> GetSiblingsAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT
                sibling.person_id,
                sibling.primary_display_name,
                sibling.sex,
                sibling.is_living,
                birt.year_from AS birth_year,
                birt_place.place_raw AS birth_place,
                deat.year_from AS death_year
            FROM genealogy.parent_child pc_self
            JOIN genealogy.parent_child pc_sib
                ON pc_sib.tree_id = pc_self.tree_id
               AND pc_sib.parent_person_id = pc_self.parent_person_id
               AND pc_sib.child_person_id <> @person_id
            JOIN genealogy.person sibling
                ON sibling.tree_id = pc_self.tree_id AND sibling.person_id = pc_sib.child_person_id
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = pc_self.tree_id AND e.person_id = sibling.person_id AND e.event_type = 'BIRT'
                ORDER BY e.year_from
                LIMIT 1
            ) birt ON true
            LEFT JOIN genealogy.place birt_place ON birt_place.place_id = birt.place_id
            LEFT JOIN LATERAL (
                SELECT e.year_from
                FROM genealogy.event e
                WHERE e.tree_id = pc_self.tree_id AND e.person_id = sibling.person_id AND e.event_type = 'DEAT'
                ORDER BY e.year_from
                LIMIT 1
            ) deat ON true
            WHERE pc_self.tree_id = @tree_id
              AND pc_self.child_person_id = @person_id
            ORDER BY birt.year_from, sibling.primary_display_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddTreeAndPerson(command, treeId, personId);

        var results = new List<RichSibling>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RichSibling(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                BirthYear: reader.GetNullableValue<short>("birth_year"),
                BirthPlace: reader.GetNullableString("birth_place"),
                DeathYear: reader.GetNullableValue<short>("death_year")));
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Marriages (mirrors ged.GetPersonFamilyContext result set 4)
    // ------------------------------------------------------------------
    private static async Task<IReadOnlyList<RichMarriage>> GetMarriagesAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                f.family_id,
                CASE WHEN f.spouse1_person_id = @person_id
                     THEN f.spouse2_person_id
                     ELSE f.spouse1_person_id
                END AS spouse_person_id,
                spouse.primary_display_name AS spouse_name,
                spouse.sex AS spouse_sex,
                spouse.is_living AS spouse_is_living,
                sp_birt.year_from AS spouse_birth_year,
                sp_birt_place.place_raw AS spouse_birth_place,
                sp_deat.year_from AS spouse_death_year,
                f.marriage_date_raw,
                f.marriage_year,
                marr_place.place_raw AS marriage_place_raw
            FROM genealogy.family f
            JOIN genealogy.person spouse
                ON spouse.tree_id = f.tree_id
               AND spouse.person_id = CASE WHEN f.spouse1_person_id = @person_id
                                            THEN f.spouse2_person_id
                                            ELSE f.spouse1_person_id
                                       END
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = f.tree_id AND e.person_id = spouse.person_id AND e.event_type = 'BIRT'
                ORDER BY e.year_from
                LIMIT 1
            ) sp_birt ON true
            LEFT JOIN genealogy.place sp_birt_place ON sp_birt_place.place_id = sp_birt.place_id
            LEFT JOIN LATERAL (
                SELECT e.year_from
                FROM genealogy.event e
                WHERE e.tree_id = f.tree_id AND e.person_id = spouse.person_id AND e.event_type = 'DEAT'
                ORDER BY e.year_from
                LIMIT 1
            ) sp_deat ON true
            LEFT JOIN genealogy.place marr_place ON marr_place.place_id = f.marriage_place_id
            WHERE f.tree_id = @tree_id
              AND (f.spouse1_person_id = @person_id OR f.spouse2_person_id = @person_id)
            ORDER BY f.marriage_year, spouse.primary_display_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddTreeAndPerson(command, treeId, personId);

        var results = new List<RichMarriage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RichMarriage(
                FamilyId: reader.GetFieldValue<Guid>(reader.GetOrdinal("family_id")),
                SpousePersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("spouse_person_id")),
                SpouseName: reader.GetNullableString("spouse_name"),
                SpouseSex: reader.GetNullableChar("spouse_sex"),
                SpouseIsLiving: reader.GetNullableValue<bool>("spouse_is_living"),
                SpouseBirthYear: reader.GetNullableValue<short>("spouse_birth_year"),
                SpouseBirthPlace: reader.GetNullableString("spouse_birth_place"),
                SpouseDeathYear: reader.GetNullableValue<short>("spouse_death_year"),
                MarriageDateRaw: reader.GetNullableString("marriage_date_raw"),
                MarriageYear: reader.GetNullableValue<short>("marriage_year"),
                MarriagePlaceRaw: reader.GetNullableString("marriage_place_raw")));
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Children (mirrors ged.GetPersonFamilyContext result set 5)
    // ------------------------------------------------------------------
    private static async Task<IReadOnlyList<RichChild>> GetChildrenAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                child.person_id,
                child.primary_display_name,
                child.sex,
                child.is_living,
                birt.year_from AS birth_year,
                birt.date_raw  AS birth_date_raw,
                birt_place.place_raw AS birth_place,
                deat.year_from AS death_year,
                other_parent.primary_display_name AS other_parent_name,
                other_parent.sex AS other_parent_sex
            FROM genealogy.parent_child pc
            JOIN genealogy.person child
                ON child.tree_id = pc.tree_id AND child.person_id = pc.child_person_id
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.date_raw, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = pc.tree_id AND e.person_id = child.person_id AND e.event_type = 'BIRT'
                ORDER BY e.year_from
                LIMIT 1
            ) birt ON true
            LEFT JOIN genealogy.place birt_place ON birt_place.place_id = birt.place_id
            LEFT JOIN LATERAL (
                SELECT e.year_from
                FROM genealogy.event e
                WHERE e.tree_id = pc.tree_id AND e.person_id = child.person_id AND e.event_type = 'DEAT'
                ORDER BY e.year_from
                LIMIT 1
            ) deat ON true
            LEFT JOIN LATERAL (
                SELECT op.primary_display_name, op.sex
                FROM genealogy.parent_child pc2
                JOIN genealogy.person op
                    ON op.tree_id = pc2.tree_id AND op.person_id = pc2.parent_person_id
                WHERE pc2.tree_id = pc.tree_id
                  AND pc2.child_person_id = child.person_id
                  AND pc2.parent_person_id <> @person_id
                LIMIT 1
            ) other_parent ON true
            WHERE pc.tree_id = @tree_id
              AND pc.parent_person_id = @person_id
            ORDER BY birt.year_from, child.primary_display_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddTreeAndPerson(command, treeId, personId);

        var results = new List<RichChild>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RichChild(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                BirthYear: reader.GetNullableValue<short>("birth_year"),
                BirthDateRaw: reader.GetNullableString("birth_date_raw"),
                BirthPlace: reader.GetNullableString("birth_place"),
                DeathYear: reader.GetNullableValue<short>("death_year"),
                OtherParentName: reader.GetNullableString("other_parent_name"),
                OtherParentSex: reader.GetNullableChar("other_parent_sex")));
        }

        return results;
    }

    private static void AddTreeAndPerson(NpgsqlCommand command, Guid treeId, Guid personId)
    {
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
    }
}
