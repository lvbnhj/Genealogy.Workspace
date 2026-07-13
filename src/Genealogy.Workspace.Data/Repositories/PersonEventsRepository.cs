using Genealogy.Workspace.Data.Models;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Reads a person's full life-event history. Ported from the SQL Server proc
/// <c>ged.GetPersonLifeEvents</c> (Database/Procedures/ged/GetPersonLifeEvents.sql):
/// same header + events shape, same DateRaw/PlaceRaw preservation, no
/// citations (the source proc does not join them either) and no DNA columns.
/// Parameterized SQL only — see docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §3.
/// </summary>
public sealed class PersonEventsRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public PersonEventsRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>Returns null when the person does not exist in the given tree.</summary>
    public async Task<PersonLifeEvents?> GetLifeEventsAsync(
        Guid treeId,
        Guid personId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var header = await ReadHeaderAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var events = await ReadEventsAsync(connection, treeId, personId, cancellationToken).ConfigureAwait(false);
        return new PersonLifeEvents(header, events);
    }

    /// <summary>
    /// Reads only the person header. Exposed <c>internal</c> so
    /// <see cref="RichFamilyContextRepository"/> can reuse it against a
    /// connection it already opened, instead of duplicating this query or
    /// opening a second connection per family-context call.
    /// </summary>
    internal static async Task<PersonLifeEventsHeader?> ReadHeaderAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT person_id, primary_display_name, sex, is_living
            FROM genealogy.person
            WHERE tree_id = @tree_id AND person_id = @person_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PersonLifeEventsHeader(
            PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
            FullName: reader.GetNullableString("primary_display_name"),
            Sex: reader.GetNullableChar("sex"),
            IsLiving: reader.GetNullableValue<bool>("is_living"));
    }

    /// <summary>
    /// Reads every life event for a person, ordered exactly as the source
    /// proc: birth year first (nulls last via <c>coalesce(year_from, 9999)</c>),
    /// then <c>date_from</c>, then a fixed event-type rank
    /// (BIRT=1, BAPT/CHR=2, MARR=3, DEAT=4, BURI=5, else=9). Exposed
    /// <c>internal</c> for the same reason as <see cref="ReadHeaderAsync"/>.
    /// </summary>
    internal static async Task<IReadOnlyList<PersonEvent>> ReadEventsAsync(
        NpgsqlConnection connection, Guid treeId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                e.event_id,
                e.external_event_key,
                e.event_type,
                e.event_value,
                e.date_raw,
                e.date_from,
                e.date_to,
                e.year_from,
                e.year_to,
                pl.place_raw,
                pl.place_normalized,
                e.family_id,
                e.related_person_id,
                related.primary_display_name AS related_person_name,
                e.is_derived,
                e.notes
            FROM genealogy.event e
            LEFT JOIN genealogy.place pl ON pl.place_id = e.place_id
            LEFT JOIN genealogy.person related
                ON related.tree_id = e.tree_id AND related.person_id = e.related_person_id
            WHERE e.tree_id = @tree_id
              AND e.person_id = @person_id
            ORDER BY
                coalesce(e.year_from, 9999::smallint),
                e.date_from,
                CASE e.event_type
                    WHEN 'BIRT' THEN 1
                    WHEN 'BAPT' THEN 2
                    WHEN 'CHR'  THEN 2
                    WHEN 'MARR' THEN 3
                    WHEN 'DEAT' THEN 4
                    WHEN 'BURI' THEN 5
                    ELSE 9
                END;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<PersonEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new PersonEvent(
                EventId: reader.GetFieldValue<long>(reader.GetOrdinal("event_id")),
                ExternalEventKey: reader.GetNullableString("external_event_key"),
                EventType: reader.GetFieldValue<string>(reader.GetOrdinal("event_type")),
                EventValue: reader.GetNullableString("event_value"),
                DateRaw: reader.GetNullableString("date_raw"),
                DateFrom: reader.GetNullableValue<DateOnly>("date_from"),
                DateTo: reader.GetNullableValue<DateOnly>("date_to"),
                YearFrom: reader.GetNullableValue<short>("year_from"),
                YearTo: reader.GetNullableValue<short>("year_to"),
                PlaceRaw: reader.GetNullableString("place_raw"),
                PlaceNormalized: reader.GetNullableString("place_normalized"),
                FamilyId: reader.GetNullableValue<Guid>("family_id"),
                RelatedPersonId: reader.GetNullableValue<Guid>("related_person_id"),
                RelatedPersonName: reader.GetNullableString("related_person_name"),
                IsDerived: reader.GetFieldValue<bool>(reader.GetOrdinal("is_derived")),
                Notes: reader.GetNullableString("notes")));
        }

        return results;
    }
}
