using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Create/correct access to <c>research.record_person_mention</c> and
/// <c>research.record_place_mention</c> (migration
/// 0009_research_evidence_schema.sql). Person mentions denormalize their
/// parent record's <c>tree_id</c> so the schema's same-tree composite FKs can
/// enforce that any accepted/candidate link stays inside that tree. App-
/// generated uuids, parameterized SQL only.
/// </summary>
public sealed class RecordMentionRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public RecordMentionRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Adds a person mention to a source record, or — when
    /// <paramref name="personMentionId"/> is supplied — corrects the
    /// descriptive fields of an existing mention on that same record instead
    /// of inserting a new one. A new mention always starts as
    /// <c>unlinked</c>; correcting an existing mention never touches its
    /// link-lifecycle fields (status/accepted_person_id/confidence — see
    /// <see cref="PersonLinkService"/>).
    /// </summary>
    /// <exception cref="SourceRecordNotFoundException">
    /// <paramref name="sourceRecordId"/> does not exist.
    /// </exception>
    /// <exception cref="PersonMentionNotFoundException">
    /// <paramref name="personMentionId"/> was supplied but does not exist on
    /// <paramref name="sourceRecordId"/>.
    /// </exception>
    public async Task<PersonMentionResult> AddPersonMentionAsync(
        Guid sourceRecordId,
        PersonMentionInput input,
        Guid? personMentionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var treeId = await GetTreeIdForRecordAsync(connection, sourceRecordId, cancellationToken)
                .ConfigureAwait(false);
            if (treeId is null)
            {
                throw new SourceRecordNotFoundException(sourceRecordId);
            }

            Guid resultMentionId;
            if (personMentionId is Guid existingId)
            {
                var updated = await UpdatePersonMentionAsync(
                    connection, existingId, sourceRecordId, input, cancellationToken).ConfigureAwait(false);
                if (!updated)
                {
                    throw new PersonMentionNotFoundException(existingId);
                }

                resultMentionId = existingId;
            }
            else
            {
                resultMentionId = await InsertPersonMentionAsync(
                    connection, sourceRecordId, treeId.Value, input, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PersonMentionResult(resultMentionId, sourceRecordId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Adds a place mention to a source record. Relies on the schema's
    /// <c>ON DELETE CASCADE</c> foreign key to reject an unknown
    /// <paramref name="sourceRecordId"/> (surfaces as a
    /// <see cref="PostgresException"/> with SqlState
    /// <see cref="PostgresErrorCodes.ForeignKeyViolation"/>) since, unlike
    /// person mentions, place mentions carry no denormalized tree_id and need
    /// no separate lookup.
    /// </summary>
    public async Task<PlaceMentionResult> AddPlaceMentionAsync(
        Guid sourceRecordId,
        PlaceMentionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.PlaceText))
        {
            throw new ArgumentException("PlaceText is required.", nameof(input));
        }

        const string sql = """
            INSERT INTO research.record_place_mention
                (place_mention_id, source_record_id, place_text, place_type, normalized_name, place_id)
            VALUES
                (@place_mention_id, @source_record_id, @place_text, @place_type, @normalized_name, @place_id);
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        var placeMentionId = Guid.NewGuid();
        command.Parameters.Add(new NpgsqlParameter("place_mention_id", NpgsqlDbType.Uuid) { Value = placeMentionId });
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("place_text", NpgsqlDbType.Text) { Value = input.PlaceText });
        command.Parameters.Add(new NpgsqlParameter("place_type", NpgsqlDbType.Text) { Value = (object?)input.PlaceType ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("normalized_name", NpgsqlDbType.Text) { Value = (object?)input.NormalizedName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("place_id", NpgsqlDbType.Bigint) { Value = (object?)input.PlaceId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new PlaceMentionResult(placeMentionId, sourceRecordId);
    }

    private static async Task<Guid?> GetTreeIdForRecordAsync(
        NpgsqlConnection connection, Guid sourceRecordId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT tree_id FROM research.source_record WHERE source_record_id = @source_record_id;";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid treeId ? treeId : null;
    }

    private static async Task<Guid> InsertPersonMentionAsync(
        NpgsqlConnection connection, Guid sourceRecordId, Guid treeId, PersonMentionInput input,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO research.record_person_mention
                (person_mention_id, tree_id, source_record_id, name_text, given_name, surname, patronymic,
                 sex, role, age_text, estimated_birth_year, social_status, relationship_text, status)
            VALUES
                (@person_mention_id, @tree_id, @source_record_id, @name_text, @given_name, @surname, @patronymic,
                 @sex, @role, @age_text, @estimated_birth_year, @social_status, @relationship_text, 'unlinked');
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        var personMentionId = Guid.NewGuid();
        command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        AddPersonMentionFieldParameters(command, input);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return personMentionId;
    }

    private static async Task<bool> UpdatePersonMentionAsync(
        NpgsqlConnection connection, Guid personMentionId, Guid sourceRecordId, PersonMentionInput input,
        CancellationToken cancellationToken)
    {
        // Scoped to (person_mention_id, source_record_id) so a caller cannot
        // accidentally "correct" a mention that belongs to a different record.
        const string sql = """
            UPDATE research.record_person_mention
            SET name_text = @name_text,
                given_name = @given_name,
                surname = @surname,
                patronymic = @patronymic,
                sex = @sex,
                role = @role,
                age_text = @age_text,
                estimated_birth_year = @estimated_birth_year,
                social_status = @social_status,
                relationship_text = @relationship_text,
                updated_at = now()
            WHERE person_mention_id = @person_mention_id
              AND source_record_id = @source_record_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        AddPersonMentionFieldParameters(command, input);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rowsAffected > 0;
    }

    private static void AddPersonMentionFieldParameters(NpgsqlCommand command, PersonMentionInput input)
    {
        command.Parameters.Add(new NpgsqlParameter("name_text", NpgsqlDbType.Text) { Value = (object?)input.NameText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("given_name", NpgsqlDbType.Text) { Value = (object?)input.GivenName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("surname", NpgsqlDbType.Text) { Value = (object?)input.Surname ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("patronymic", NpgsqlDbType.Text) { Value = (object?)input.Patronymic ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("sex", NpgsqlDbType.Text) { Value = input.Sex is null ? DBNull.Value : input.Sex.Value.ToString() });
        command.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Text) { Value = (object?)input.Role ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("age_text", NpgsqlDbType.Text) { Value = (object?)input.AgeText ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("estimated_birth_year", NpgsqlDbType.Smallint) { Value = (object?)input.EstimatedBirthYear ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("social_status", NpgsqlDbType.Text) { Value = (object?)input.SocialStatus ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("relationship_text", NpgsqlDbType.Text) { Value = (object?)input.RelationshipText ?? DBNull.Value });
    }
}
