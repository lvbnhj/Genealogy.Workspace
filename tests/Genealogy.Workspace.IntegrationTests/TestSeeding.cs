using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Shared raw-insert helpers for Phase 2 integration tests. These bypass the
/// repository layer on purpose: the constraint, search, family-context, schema
/// and index-usage tests all need to seed rows that no repository exposes a
/// write path for yet (person, person_name, family, parent_child), and the
/// constraint tests specifically need to reach the database directly so a
/// <see cref="Npgsql.PostgresException"/> surfaces without any repository
/// translation in the way.
/// </summary>
internal static class TestSeeding
{
    /// <summary>Generates a unique <c>gw_test_&lt;hex&gt;</c> database name, matching the
    /// convention used by <c>DatabaseLifecycleTests</c>.</summary>
    public static string NewTestDatabaseName() => "gw_test_" + RandomHex(12);

    public static string RandomHex(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes((length + 1) / 2);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }

    public static async Task InsertTreeAsync(
        NpgsqlConnection connection,
        Guid treeId,
        string name,
        bool isDefault = false,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO genealogy.tree (tree_id, name, description, is_default)
            VALUES (@tree_id, @name, @description, @is_default);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
        command.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("is_default", NpgsqlDbType.Boolean) { Value = isDefault });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertPersonAsync(
        NpgsqlConnection connection,
        Guid treeId,
        Guid personId,
        string? externalId = null,
        string? primaryDisplayName = null,
        string? surnameNormalized = null,
        char? sex = null,
        bool? isLiving = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO genealogy.person
                (person_id, tree_id, external_id, sex, is_living, primary_display_name, surname_normalized)
            VALUES
                (@person_id, @tree_id, @external_id, @sex, @is_living, @primary_display_name, @surname_normalized);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("external_id", NpgsqlDbType.Text) { Value = (object?)externalId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("sex", NpgsqlDbType.Text) { Value = sex is null ? DBNull.Value : sex.Value.ToString() });
        command.Parameters.Add(new NpgsqlParameter("is_living", NpgsqlDbType.Boolean) { Value = (object?)isLiving ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("primary_display_name", NpgsqlDbType.Text) { Value = (object?)primaryDisplayName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("surname_normalized", NpgsqlDbType.Text) { Value = (object?)surnameNormalized ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertPersonNameAsync(
        NpgsqlConnection connection,
        Guid treeId,
        Guid personId,
        string scriptCode,
        string nameType,
        string fullName,
        string fullNameNormalized,
        bool isPrimary = false,
        string? given = null,
        string? surname = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO genealogy.person_name
                (tree_id, person_id, script_code, name_type, given, surname, full_name, full_name_normalized, is_primary)
            VALUES
                (@tree_id, @person_id, @script_code, @name_type, @given, @surname, @full_name, @full_name_normalized, @is_primary);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("script_code", NpgsqlDbType.Text) { Value = scriptCode });
        command.Parameters.Add(new NpgsqlParameter("name_type", NpgsqlDbType.Text) { Value = nameType });
        command.Parameters.Add(new NpgsqlParameter("given", NpgsqlDbType.Text) { Value = (object?)given ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("surname", NpgsqlDbType.Text) { Value = (object?)surname ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("full_name", NpgsqlDbType.Text) { Value = fullName });
        command.Parameters.Add(new NpgsqlParameter("full_name_normalized", NpgsqlDbType.Text) { Value = fullNameNormalized });
        command.Parameters.Add(new NpgsqlParameter("is_primary", NpgsqlDbType.Boolean) { Value = isPrimary });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertFamilyAsync(
        NpgsqlConnection connection,
        Guid treeId,
        Guid familyId,
        Guid spouse1PersonId,
        Guid spouse2PersonId,
        short? marriageYear = null,
        string? marriageDateRaw = null,
        long? marriagePlaceId = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO genealogy.family
                (family_id, tree_id, spouse1_person_id, spouse2_person_id,
                 marriage_year, marriage_date_raw, marriage_place_id)
            VALUES
                (@family_id, @tree_id, @spouse1_person_id, @spouse2_person_id,
                 @marriage_year, @marriage_date_raw, @marriage_place_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("family_id", NpgsqlDbType.Uuid) { Value = familyId });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("spouse1_person_id", NpgsqlDbType.Uuid) { Value = spouse1PersonId });
        command.Parameters.Add(new NpgsqlParameter("spouse2_person_id", NpgsqlDbType.Uuid) { Value = spouse2PersonId });
        command.Parameters.Add(new NpgsqlParameter("marriage_year", NpgsqlDbType.Smallint) { Value = (object?)marriageYear ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("marriage_date_raw", NpgsqlDbType.Text) { Value = (object?)marriageDateRaw ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("marriage_place_id", NpgsqlDbType.Bigint) { Value = (object?)marriagePlaceId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertParentChildAsync(
        NpgsqlConnection connection,
        Guid treeId,
        Guid parentPersonId,
        Guid childPersonId,
        string relationType = "BIO",
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO genealogy.parent_child (tree_id, parent_person_id, child_person_id, relation_type)
            VALUES (@tree_id, @parent_person_id, @child_person_id, @relation_type);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("parent_person_id", NpgsqlDbType.Uuid) { Value = parentPersonId });
        command.Parameters.Add(new NpgsqlParameter("child_person_id", NpgsqlDbType.Uuid) { Value = childPersonId });
        command.Parameters.Add(new NpgsqlParameter("relation_type", NpgsqlDbType.Text) { Value = relationType });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts a global <c>genealogy.place</c> row and returns its generated id.</summary>
    public static async Task<long> InsertPlaceAsync(
        NpgsqlConnection connection,
        string placeRaw,
        string? placeNormalized = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO genealogy.place (place_raw, place_normalized)
            VALUES (@place_raw, @place_normalized)
            RETURNING place_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("place_raw", NpgsqlDbType.Text) { Value = placeRaw });
        command.Parameters.Add(new NpgsqlParameter("place_normalized", NpgsqlDbType.Text) { Value = (object?)placeNormalized ?? DBNull.Value });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Inserts a <c>genealogy.event</c> row for Phase 5's life-events / family-context / search tests.</summary>
    public static async Task InsertEventAsync(
        NpgsqlConnection connection,
        Guid treeId,
        Guid personId,
        string eventType,
        short? yearFrom = null,
        short? yearTo = null,
        string? dateRaw = null,
        DateOnly? dateFrom = null,
        long? placeId = null,
        Guid? familyId = null,
        Guid? relatedPersonId = null,
        string? externalEventKey = null,
        string? eventValue = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO genealogy.event
                (tree_id, person_id, event_type, event_value, date_raw, date_from, year_from, year_to,
                 place_id, family_id, related_person_id, external_event_key, notes)
            VALUES
                (@tree_id, @person_id, @event_type, @event_value, @date_raw, @date_from, @year_from, @year_to,
                 @place_id, @family_id, @related_person_id, @external_event_key, @notes);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = eventType });
        command.Parameters.Add(new NpgsqlParameter("event_value", NpgsqlDbType.Text) { Value = (object?)eventValue ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("date_raw", NpgsqlDbType.Text) { Value = (object?)dateRaw ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("date_from", NpgsqlDbType.Date) { Value = (object?)dateFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("year_from", NpgsqlDbType.Smallint) { Value = (object?)yearFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("year_to", NpgsqlDbType.Smallint) { Value = (object?)yearTo ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("place_id", NpgsqlDbType.Bigint) { Value = (object?)placeId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("family_id", NpgsqlDbType.Uuid) { Value = (object?)familyId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("related_person_id", NpgsqlDbType.Uuid) { Value = (object?)relatedPersonId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("external_event_key", NpgsqlDbType.Varchar) { Value = (object?)externalEventKey ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Text) { Value = (object?)notes ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
