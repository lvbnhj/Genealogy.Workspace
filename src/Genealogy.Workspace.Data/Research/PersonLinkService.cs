using System.Globalization;
using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// MVP link-suggestion scorer plus the accept/reject lifecycle for
/// <c>research.person_link_candidate</c> (migration
/// 0009_research_evidence_schema.sql). <see cref="SuggestLinksAsync"/> is
/// READ-ONLY against <c>genealogy.*</c> — it never inserts/updates/deletes a
/// tree row; it only reads candidate persons and writes to the
/// <c>research</c> schema. Parameterized SQL only.
/// </summary>
public sealed class PersonLinkService
{
    /// <summary>How many candidate genealogy.person rows to pull from the
    /// database before scoring/ranking in memory. Bounds the query; the
    /// caller-facing result is further limited to topN.</summary>
    private const int CandidatePoolLimit = 200;

    private readonly NpgsqlConnectionFactory _connectionFactory;

    public PersonLinkService(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Scores candidate <c>genealogy.person</c> rows in the mention's own
    /// tree against the mention's surname/given name/estimated birth year,
    /// persists each surviving candidate as a
    /// <c>research.person_link_candidate</c> row (status <c>suggested</c>),
    /// and returns them ordered by score descending.
    ///
    /// Scoring (each component only contributes when both sides have data;
    /// components are additive and the weights sum to at most 1.0):
    ///   - surname: exact match (case/whitespace-insensitive) +0.50, partial
    ///     (substring either direction) +0.25;
    ///   - given name: exact +0.30, partial +0.15;
    ///   - birth year proximity vs the candidate's earliest BIRT event:
    ///     |Δyear| ≤ 1 → +0.20, ≤ 3 → +0.10, ≤ 7 → +0.05.
    /// A candidate with a total score of 0 (no signal matched at all) is
    /// dropped, never persisted.
    ///
    /// A (person_mention_id, person_id) pair that already has a 'rejected'
    /// candidate row is never recreated — the rejection survives re-suggestion.
    /// A pair with an existing non-rejected row is refreshed in place
    /// (score/explanation updated) when still 'suggested', or returned
    /// unchanged when already 'accepted'/'superseded', rather than duplicated.
    /// </summary>
    /// <exception cref="PersonMentionNotFoundException"><paramref name="personMentionId"/> does not exist.</exception>
    public async Task<IReadOnlyList<PersonLinkCandidateResult>> SuggestLinksAsync(
        Guid personMentionId,
        int topN = 10,
        CancellationToken cancellationToken = default)
    {
        var effectiveTopN = Math.Clamp(topN, 1, 100);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var mention = await LoadMentionAsync(connection, personMentionId, cancellationToken)
                .ConfigureAwait(false);
            if (mention is null)
            {
                throw new PersonMentionNotFoundException(personMentionId);
            }

            if (mention.Surname is null && mention.GivenName is null)
            {
                // Nothing to score against — no name signal at all.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Array.Empty<PersonLinkCandidateResult>();
            }

            var candidates = await LoadCandidatePersonsAsync(
                connection, mention, cancellationToken).ConfigureAwait(false);

            var scored = candidates
                .Select(c => Score(mention, c))
                .Where(s => s.Score > 0m)
                .OrderByDescending(s => s.Score)
                .Take(effectiveTopN)
                .ToList();

            var results = new List<PersonLinkCandidateResult>(scored.Count);
            foreach (var scoredCandidate in scored)
            {
                var persisted = await PersistCandidateAsync(
                    connection, personMentionId, mention.TreeId, scoredCandidate, cancellationToken)
                    .ConfigureAwait(false);
                if (persisted is not null)
                {
                    results.Add(persisted);
                }
            }

            if (results.Count > 0 && mention.Status == "unlinked")
            {
                await SetMentionStatusAsync(
                    connection, personMentionId, "suggested", cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return results;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Accepts a candidate: marks it <c>accepted</c>, sets the parent
    /// mention's <c>accepted_person_id</c>/<c>status</c>/<c>confidence</c>,
    /// and supersedes the mention's other still-<c>suggested</c> candidates.
    /// </summary>
    /// <exception cref="PersonLinkCandidateNotFoundException"><paramref name="personLinkCandidateId"/> does not exist.</exception>
    public async Task<AcceptLinkResult> AcceptLinkAsync(
        Guid personLinkCandidateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var candidate = await LoadCandidateForDecisionAsync(
                connection, personLinkCandidateId, cancellationToken).ConfigureAwait(false);
            if (candidate is null)
            {
                throw new PersonLinkCandidateNotFoundException(personLinkCandidateId);
            }

            const string acceptCandidateSql = """
                UPDATE research.person_link_candidate
                SET status = 'accepted', decided_at = now()
                WHERE person_link_candidate_id = @person_link_candidate_id;
                """;
            await using (var command = new NpgsqlCommand(acceptCandidateSql, connection))
            {
                command.Parameters.Add(new NpgsqlParameter("person_link_candidate_id", NpgsqlDbType.Uuid) { Value = personLinkCandidateId });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            const string acceptMentionSql = """
                UPDATE research.record_person_mention
                SET accepted_person_id = @person_id, status = 'accepted', confidence = @score, updated_at = now()
                WHERE person_mention_id = @person_mention_id;
                """;
            await using (var command = new NpgsqlCommand(acceptMentionSql, connection))
            {
                command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = candidate.PersonId });
                command.Parameters.Add(new NpgsqlParameter("score", NpgsqlDbType.Numeric) { Value = candidate.Score });
                command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = candidate.PersonMentionId });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            const string supersedeSql = """
                UPDATE research.person_link_candidate
                SET status = 'superseded', decided_at = now()
                WHERE person_mention_id = @person_mention_id
                  AND person_link_candidate_id <> @person_link_candidate_id
                  AND status = 'suggested';
                """;
            await using (var command = new NpgsqlCommand(supersedeSql, connection))
            {
                command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = candidate.PersonMentionId });
                command.Parameters.Add(new NpgsqlParameter("person_link_candidate_id", NpgsqlDbType.Uuid) { Value = personLinkCandidateId });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AcceptLinkResult(candidate.PersonMentionId, candidate.PersonId, "accepted");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Rejects a candidate. Never touches the parent mention's accepted
    /// link. The rejected row is left in place (not deleted) so
    /// <see cref="SuggestLinksAsync"/> never recreates it for the same
    /// (person_mention_id, person_id) pair.
    /// </summary>
    /// <exception cref="PersonLinkCandidateNotFoundException"><paramref name="personLinkCandidateId"/> does not exist.</exception>
    public async Task<RejectLinkResult> RejectLinkAsync(
        Guid personLinkCandidateId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE research.person_link_candidate
            SET status = 'rejected', decided_at = now()
            WHERE person_link_candidate_id = @person_link_candidate_id
            RETURNING person_link_candidate_id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_link_candidate_id", NpgsqlDbType.Uuid) { Value = personLinkCandidateId });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new PersonLinkCandidateNotFoundException(personLinkCandidateId);
        }

        return new RejectLinkResult(personLinkCandidateId, "rejected");
    }

    // -- mention loading --------------------------------------------------

    private sealed record MentionForScoring(
        Guid PersonMentionId, Guid TreeId, string? Surname, string? GivenName,
        short? EstimatedBirthYear, string Status);

    private static async Task<MentionForScoring?> LoadMentionAsync(
        NpgsqlConnection connection, Guid personMentionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT person_mention_id, tree_id, surname, given_name, estimated_birth_year, status
            FROM research.record_person_mention
            WHERE person_mention_id = @person_mention_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MentionForScoring(
            PersonMentionId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_mention_id")),
            TreeId: reader.GetFieldValue<Guid>(reader.GetOrdinal("tree_id")),
            Surname: reader.GetNullableString("surname"),
            GivenName: reader.GetNullableString("given_name"),
            EstimatedBirthYear: reader.GetNullableValue<short>("estimated_birth_year"),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")));
    }

    // -- candidate person loading (READ-ONLY against genealogy.*) ----------

    private sealed record CandidatePerson(
        Guid PersonId, string? FullName, string? SurnameNormalized, string? GivenName, short? BirthYear);

    /// <summary>
    /// Pulls candidate persons from the mention's tree, bounded by
    /// <see cref="CandidatePoolLimit"/>. Only SELECTs — never writes to
    /// <c>genealogy.*</c>. A person is a candidate if its normalized surname
    /// or its primary given name loosely overlaps the mention's surname/given
    /// name; exact-vs-partial distinction and the final score are computed in
    /// <see cref="Score"/>.
    /// </summary>
    private static async Task<IReadOnlyList<CandidatePerson>> LoadCandidatePersonsAsync(
        NpgsqlConnection connection, MentionForScoring mention, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.person_id, p.primary_display_name, p.surname_normalized, pn.given AS given_name, birt.birth_year
            FROM genealogy.person p
            LEFT JOIN LATERAL (
                SELECT given
                FROM genealogy.person_name
                WHERE tree_id = p.tree_id AND person_id = p.person_id
                ORDER BY is_primary DESC, person_name_id
                LIMIT 1
            ) pn ON true
            LEFT JOIN LATERAL (
                SELECT coalesce(e.year_from, e.year_to) AS birth_year
                FROM genealogy.event e
                WHERE e.tree_id = p.tree_id AND e.person_id = p.person_id AND e.event_type = 'BIRT'
                ORDER BY e.year_from
                LIMIT 1
            ) birt ON true
            WHERE p.tree_id = @tree_id
              AND (
                    (@surname_pattern IS NOT NULL AND p.surname_normalized ILIKE @surname_pattern ESCAPE '\')
                    OR (@given_pattern IS NOT NULL AND pn.given ILIKE @given_pattern ESCAPE '\')
                  )
            LIMIT @candidate_pool_limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = mention.TreeId });
        command.Parameters.Add(new NpgsqlParameter("surname_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(mention.Surname) });
        command.Parameters.Add(new NpgsqlParameter("given_pattern", NpgsqlDbType.Text) { Value = ToPatternOrNull(mention.GivenName) });
        command.Parameters.Add(new NpgsqlParameter("candidate_pool_limit", NpgsqlDbType.Integer) { Value = CandidatePoolLimit });

        var results = new List<CandidatePerson>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CandidatePerson(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                FullName: reader.GetNullableString("primary_display_name"),
                SurnameNormalized: reader.GetNullableString("surname_normalized"),
                GivenName: reader.GetNullableString("given_name"),
                BirthYear: reader.GetNullableValue<short>("birth_year")));
        }

        return results;
    }

    // -- scoring ------------------------------------------------------------

    private sealed record ScoredCandidate(Guid PersonId, string? FullName, decimal Score, string Explanation);

    private static ScoredCandidate Score(MentionForScoring mention, CandidatePerson candidate)
    {
        var notes = new List<string>();
        decimal total = 0m;

        var (surnameScore, surnameNote) = ScoreText(
            mention.Surname, candidate.SurnameNormalized, exactWeight: 0.50m, partialWeight: 0.25m, "surname");
        total += surnameScore;
        if (surnameNote is not null)
        {
            notes.Add(surnameNote);
        }

        var (givenScore, givenNote) = ScoreText(
            mention.GivenName, candidate.GivenName, exactWeight: 0.30m, partialWeight: 0.15m, "given name");
        total += givenScore;
        if (givenNote is not null)
        {
            notes.Add(givenNote);
        }

        var (yearScore, yearNote) = ScoreBirthYear(mention.EstimatedBirthYear, candidate.BirthYear);
        total += yearScore;
        if (yearNote is not null)
        {
            notes.Add(yearNote);
        }

        var clamped = Math.Min(1.0m, total);
        var explanation = notes.Count > 0
            ? string.Join("; ", notes)
            : "No matching signal.";

        return new ScoredCandidate(candidate.PersonId, candidate.FullName, clamped, explanation);
    }

    private static (decimal Score, string? Note) ScoreText(
        string? mentionValue, string? candidateValue, decimal exactWeight, decimal partialWeight, string label)
    {
        var a = Normalize(mentionValue);
        var b = Normalize(candidateValue);
        if (a is null || b is null)
        {
            return (0m, null);
        }

        if (a == b)
        {
            return (exactWeight, $"{label} '{mentionValue}' matches '{candidateValue}' exactly (+{exactWeight.ToString(CultureInfo.InvariantCulture)})");
        }

        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            return (partialWeight, $"{label} '{mentionValue}' partially matches '{candidateValue}' (+{partialWeight.ToString(CultureInfo.InvariantCulture)})");
        }

        return (0m, null);
    }

    private static (decimal Score, string? Note) ScoreBirthYear(short? mentionYear, short? candidateYear)
    {
        if (mentionYear is null || candidateYear is null)
        {
            return (0m, null);
        }

        var delta = Math.Abs(mentionYear.Value - candidateYear.Value);
        return delta switch
        {
            <= 1 => (0.20m, $"birth year {mentionYear} vs {candidateYear} (Δ{delta}, +0.20)"),
            <= 3 => (0.10m, $"birth year {mentionYear} vs {candidateYear} (Δ{delta}, +0.10)"),
            <= 7 => (0.05m, $"birth year {mentionYear} vs {candidateYear} (Δ{delta}, +0.05)"),
            _ => (0m, null),
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static object ToPatternOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : "%" + EscapeLikePattern(value.Trim()) + "%";

    private static string EscapeLikePattern(string input) =>
        input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // -- persistence (research schema only) ---------------------------------

    /// <summary>
    /// Persists one scored candidate as a <c>person_link_candidate</c> row,
    /// honoring the rejected-survives rule: a pair with an existing
    /// 'rejected' row is skipped entirely (returns null); a pair with an
    /// existing 'suggested' row is refreshed in place; a pair already
    /// 'accepted'/'superseded' is returned unchanged; otherwise a new row is
    /// inserted.
    /// </summary>
    private static async Task<PersonLinkCandidateResult?> PersistCandidateAsync(
        NpgsqlConnection connection, Guid personMentionId, Guid treeId, ScoredCandidate scored,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingCandidateAsync(
            connection, personMentionId, scored.PersonId, cancellationToken).ConfigureAwait(false);

        if (existing is { Status: "rejected" })
        {
            return null;
        }

        if (existing is null)
        {
            const string insertSql = """
                INSERT INTO research.person_link_candidate
                    (person_link_candidate_id, person_mention_id, tree_id, person_id, score, explanation, status)
                VALUES
                    (@person_link_candidate_id, @person_mention_id, @tree_id, @person_id, @score, @explanation, 'suggested');
                """;

            var newId = Guid.NewGuid();
            await using var command = new NpgsqlCommand(insertSql, connection);
            command.Parameters.Add(new NpgsqlParameter("person_link_candidate_id", NpgsqlDbType.Uuid) { Value = newId });
            command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });
            command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
            command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = scored.PersonId });
            command.Parameters.Add(new NpgsqlParameter("score", NpgsqlDbType.Numeric) { Value = scored.Score });
            command.Parameters.Add(new NpgsqlParameter("explanation", NpgsqlDbType.Text) { Value = scored.Explanation });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return new PersonLinkCandidateResult(newId, scored.PersonId, scored.FullName, scored.Score, scored.Explanation);
        }

        if (existing.Status == "suggested")
        {
            const string updateSql = """
                UPDATE research.person_link_candidate
                SET score = @score, explanation = @explanation
                WHERE person_link_candidate_id = @person_link_candidate_id;
                """;

            await using var command = new NpgsqlCommand(updateSql, connection);
            command.Parameters.Add(new NpgsqlParameter("score", NpgsqlDbType.Numeric) { Value = scored.Score });
            command.Parameters.Add(new NpgsqlParameter("explanation", NpgsqlDbType.Text) { Value = scored.Explanation });
            command.Parameters.Add(new NpgsqlParameter("person_link_candidate_id", NpgsqlDbType.Uuid) { Value = existing.PersonLinkCandidateId });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return new PersonLinkCandidateResult(existing.PersonLinkCandidateId, scored.PersonId, scored.FullName, scored.Score, scored.Explanation);
        }

        // accepted / superseded: leave the decided row untouched, surface it as-is.
        return new PersonLinkCandidateResult(
            existing.PersonLinkCandidateId, scored.PersonId, scored.FullName, existing.Score, existing.Explanation ?? scored.Explanation);
    }

    private sealed record ExistingCandidate(Guid PersonLinkCandidateId, string Status, decimal Score, string? Explanation);

    private static async Task<ExistingCandidate?> FindExistingCandidateAsync(
        NpgsqlConnection connection, Guid personMentionId, Guid personId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT person_link_candidate_id, status, score, explanation
            FROM research.person_link_candidate
            WHERE person_mention_id = @person_mention_id AND person_id = @person_id
            ORDER BY created_at DESC
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExistingCandidate(
            PersonLinkCandidateId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_link_candidate_id")),
            Status: reader.GetFieldValue<string>(reader.GetOrdinal("status")),
            Score: reader.GetFieldValue<decimal>(reader.GetOrdinal("score")),
            Explanation: reader.GetNullableString("explanation"));
    }

    private static async Task SetMentionStatusAsync(
        NpgsqlConnection connection, Guid personMentionId, string status, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE research.record_person_mention
            SET status = @status, updated_at = now()
            WHERE person_mention_id = @person_mention_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = status });
        command.Parameters.Add(new NpgsqlParameter("person_mention_id", NpgsqlDbType.Uuid) { Value = personMentionId });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // -- candidate loading for accept/reject --------------------------------

    private sealed record CandidateForDecision(Guid PersonMentionId, Guid PersonId, decimal Score);

    private static async Task<CandidateForDecision?> LoadCandidateForDecisionAsync(
        NpgsqlConnection connection, Guid personLinkCandidateId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT person_mention_id, person_id, score
            FROM research.person_link_candidate
            WHERE person_link_candidate_id = @person_link_candidate_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("person_link_candidate_id", NpgsqlDbType.Uuid) { Value = personLinkCandidateId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CandidateForDecision(
            PersonMentionId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_mention_id")),
            PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
            Score: reader.GetFieldValue<decimal>(reader.GetOrdinal("score")));
    }
}
