using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Traversal;

/// <summary>
/// Recursive / graph traversal queries over the tree-scoped genealogy edges
/// (<c>genealogy.parent_child</c>, <c>genealogy.family</c>) enriched from
/// <c>genealogy.person</c>, <c>genealogy.event</c> and <c>genealogy.place</c>.
///
/// Ported from the SQL Server <c>ged.*</c> procedures listed on each method,
/// with three project-wide changes applied everywhere:
///   1. reads <c>genealogy.parent_child</c> (parent_person_id / child_person_id)
///      rather than <c>ged.TreeParentOf</c> / <c>ged.TreeRelationship</c>;
///   2. drops every DNA column the source surfaced (no <c>TreePersonLink</c> /
///      <c>dbo.Person</c> joins);
///   3. adds explicit cycle protection the source procs mostly lacked — every
///      recursive CTE carries a <c>uuid[]</c> visited/path array and guards the
///      next hop with <c>NOT (&lt;next id&gt; = ANY(&lt;path&gt;))</c>, so a cyclic
///      <c>parent_child</c> graph terminates cleanly at the natural frontier
///      instead of spinning up to the depth cap.
///
/// Every query is scoped to a single <c>tree_id</c>, and all
/// <c>parent_child</c> edges are traversed regardless of <c>relation_type</c>
/// (only 'BIO' is populated today). Parameterized SQL only. See
/// docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10 Phase 5.
/// </summary>
public sealed class TreeTraversalRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public TreeTraversalRepository(NpgsqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Walks upward from <paramref name="personId"/> via
    /// <c>parent_child</c> (child_person_id -&gt; parent_person_id), returning
    /// every ancestor up to <paramref name="maxGenerations"/> generations back.
    /// Generation is 1-based (1 = direct parent). Ported from
    /// <c>ged.GetAncestors</c>.
    /// </summary>
    public async Task<IReadOnlyList<AncestorRow>> GetAncestorsAsync(
        Guid treeId,
        Guid personId,
        int maxGenerations = 6,
        CancellationToken cancellationToken = default)
    {
        // The visited array is seeded with the starting person and grows by the
        // parent id at each hop; the guard `NOT (parent = ANY(path))` stops a
        // cyclic parent_child graph (A->B->C->A) from looping.
        const string sql = """
            WITH RECURSIVE ancestors AS (
                SELECT
                    pc.parent_person_id AS ancestor_id,
                    1 AS generation,
                    ARRAY[pc.child_person_id, pc.parent_person_id] AS path
                FROM genealogy.parent_child pc
                WHERE pc.tree_id = @tree_id
                  AND pc.child_person_id = @person_id

                UNION ALL

                SELECT
                    pc.parent_person_id,
                    a.generation + 1,
                    a.path || pc.parent_person_id
                FROM ancestors a
                JOIN genealogy.parent_child pc
                    ON pc.tree_id = @tree_id
                   AND pc.child_person_id = a.ancestor_id
                WHERE a.generation < @max_generations
                  AND NOT (pc.parent_person_id = ANY(a.path))
            )
            SELECT DISTINCT
                a.ancestor_id AS person_id,
                p.primary_display_name AS full_name,
                p.sex,
                p.is_living,
                a.generation,
                birth.year_from::int AS birth_year,
                birth.date_raw AS birth_date_raw,
                bp.place_raw AS birth_place,
                death.year_from::int AS death_year,
                dp.place_raw AS death_place
            FROM ancestors a
            JOIN genealogy.person p
                ON p.tree_id = @tree_id AND p.person_id = a.ancestor_id
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.date_raw, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = @tree_id
                  AND e.person_id = a.ancestor_id
                  AND e.event_type IN ('BIRT', 'CHR')
                ORDER BY CASE WHEN e.event_type = 'BIRT' THEN 0 ELSE 1 END,
                         e.year_from NULLS LAST, e.event_id
                LIMIT 1
            ) birth ON true
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = @tree_id
                  AND e.person_id = a.ancestor_id
                  AND e.event_type = 'DEAT'
                ORDER BY e.year_from NULLS LAST, e.event_id
                LIMIT 1
            ) death ON true
            LEFT JOIN genealogy.place bp ON bp.place_id = birth.place_id
            LEFT JOIN genealogy.place dp ON dp.place_id = death.place_id
            ORDER BY a.generation, full_name NULLS LAST;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("max_generations", NpgsqlDbType.Integer) { Value = maxGenerations });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<AncestorRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AncestorRow(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                FullName: reader.GetNullableString("full_name"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                Generation: reader.GetFieldValue<int>(reader.GetOrdinal("generation")),
                BirthYear: reader.GetNullableValue<int>("birth_year"),
                BirthDateRaw: reader.GetNullableString("birth_date_raw"),
                BirthPlace: reader.GetNullableString("birth_place"),
                DeathYear: reader.GetNullableValue<int>("death_year"),
                DeathPlace: reader.GetNullableString("death_place")));
        }

        return results;
    }

    /// <summary>
    /// Walks downward from <paramref name="ancestorId"/> via
    /// <c>parent_child</c> (parent_person_id -&gt; child_person_id), returning
    /// every descendant up to <paramref name="maxGenerations"/> deep. The
    /// ancestor (generation 0) is excluded. Ported from
    /// <c>ged.GetDescendants</c>, DNA columns dropped.
    /// </summary>
    public async Task<IReadOnlyList<DescendantRow>> GetDescendantsAsync(
        Guid treeId,
        Guid ancestorId,
        int maxGenerations = 8,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH RECURSIVE descendants AS (
                SELECT
                    pc.child_person_id AS person_id,
                    pc.parent_person_id AS parent_person_id,
                    1 AS generation,
                    ARRAY[pc.parent_person_id, pc.child_person_id] AS path
                FROM genealogy.parent_child pc
                WHERE pc.tree_id = @tree_id
                  AND pc.parent_person_id = @ancestor_id

                UNION ALL

                SELECT
                    pc.child_person_id,
                    pc.parent_person_id,
                    d.generation + 1,
                    d.path || pc.child_person_id
                FROM descendants d
                JOIN genealogy.parent_child pc
                    ON pc.tree_id = @tree_id
                   AND pc.parent_person_id = d.person_id
                WHERE d.generation < @max_generations
                  AND NOT (pc.child_person_id = ANY(d.path))
            )
            SELECT
                d.person_id,
                p.primary_display_name,
                p.sex,
                p.is_living,
                d.generation,
                d.parent_person_id
            FROM descendants d
            JOIN genealogy.person p
                ON p.tree_id = @tree_id AND p.person_id = d.person_id
            ORDER BY d.generation, p.primary_display_name NULLS LAST, d.person_id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("ancestor_id", NpgsqlDbType.Uuid) { Value = ancestorId });
        command.Parameters.Add(new NpgsqlParameter("max_generations", NpgsqlDbType.Integer) { Value = maxGenerations });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<DescendantRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DescendantRow(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                PrimaryDisplayName: reader.GetNullableString("primary_display_name"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                Generation: reader.GetFieldValue<int>(reader.GetOrdinal("generation")),
                ParentPersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("parent_person_id"))));
        }

        return results;
    }

    /// <summary>
    /// Blood descendants of <paramref name="ancestorId"/> alive at
    /// <paramref name="year"/> (born on/before the year AND either no death year
    /// or died on/after it), unioned with the spouses of those descendants.
    /// Ported from <c>ged.GetDescendantsAtYear</c>.
    ///
    /// SPOUSE INCLUSION (user decision): a spouse is included through their
    /// marriage to an in-selection descendant regardless of the spouse's own
    /// birth/death years — the source's extra spouse death-year filter is
    /// intentionally NOT ported. The spouse's years are still returned for
    /// information.
    ///
    /// SPOUSE DUPLICATION FIX: the source fanned out one spouse row per raw
    /// recursion row for the married descendant, so a descendant reached through
    /// multiple ancestral paths (common in endogamous trees) duplicated every
    /// spouse. Here the descendant recursion is first collapsed to one row per
    /// person (<c>distinct_desc</c>, keeping the minimum generation and its
    /// parent) and spouses are derived from that de-duplicated set, so each
    /// (descendant, marriage) yields exactly one spouse row while spouse-via-
    /// marriage inclusion is preserved. Collapsing also removes the same
    /// endogamy-driven duplication from the DESCENDANT rows.
    /// </summary>
    public async Task<IReadOnlyList<DescendantAtYearRow>> GetDescendantsAtYearAsync(
        Guid treeId,
        Guid ancestorId,
        int year,
        int maxGenerations = 8,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH RECURSIVE descendants AS (
                SELECT
                    pc.child_person_id AS person_id,
                    pc.parent_person_id AS parent_person_id,
                    1 AS generation,
                    ARRAY[pc.parent_person_id, pc.child_person_id] AS path
                FROM genealogy.parent_child pc
                WHERE pc.tree_id = @tree_id
                  AND pc.parent_person_id = @ancestor_id

                UNION ALL

                SELECT
                    pc.child_person_id,
                    pc.parent_person_id,
                    d.generation + 1,
                    d.path || pc.child_person_id
                FROM descendants d
                JOIN genealogy.parent_child pc
                    ON pc.tree_id = @tree_id
                   AND pc.parent_person_id = d.person_id
                WHERE d.generation < @max_generations
                  AND NOT (pc.child_person_id = ANY(d.path))
            ),
            -- Collapse endogamy-driven duplicates: one row per descendant,
            -- keeping the minimum generation and the parent from that path.
            distinct_desc AS (
                SELECT
                    person_id,
                    MIN(generation) AS generation,
                    (ARRAY_AGG(parent_person_id ORDER BY generation, parent_person_id))[1] AS parent_person_id
                FROM descendants
                GROUP BY person_id
            ),
            desc_years AS (
                SELECT
                    dd.person_id,
                    dd.generation,
                    dd.parent_person_id,
                    y.birth_year,
                    y.death_year
                FROM distinct_desc dd
                LEFT JOIN LATERAL (
                    SELECT
                        MIN(CASE WHEN e.event_type = 'BIRT' THEN COALESCE(e.year_from, e.year_to) END)::int AS birth_year,
                        MIN(CASE WHEN e.event_type = 'DEAT' THEN COALESCE(e.year_from, e.year_to) END)::int AS death_year
                    FROM genealogy.event e
                    WHERE e.tree_id = @tree_id
                      AND e.person_id = dd.person_id
                      AND e.event_type IN ('BIRT', 'DEAT')
                ) y ON true
            ),
            alive_desc AS (
                SELECT *
                FROM desc_years
                WHERE (birth_year IS NULL OR birth_year <= @year)
                  AND (death_year IS NULL OR death_year >= @year)
            )
            -- Descendant rows.
            SELECT
                ad.person_id,
                p.primary_display_name AS full_name,
                p.sex,
                'DESCENDANT' AS person_type,
                ad.generation,
                ad.parent_person_id,
                NULL::uuid AS spouse_of_person_id,
                ad.birth_year,
                ad.death_year
            FROM alive_desc ad
            JOIN genealogy.person p
                ON p.tree_id = @tree_id AND p.person_id = ad.person_id

            UNION ALL

            -- Spouse rows: the OTHER member of any family the alive descendant
            -- belongs to (married on/before @year, or unknown marriage year).
            -- alive_desc is already one row per descendant, so no fan-out.
            SELECT DISTINCT
                sp.person_id,
                sp.primary_display_name AS full_name,
                sp.sex,
                'SPOUSE' AS person_type,
                ad.generation,
                ad.parent_person_id,
                ad.person_id AS spouse_of_person_id,
                sy.birth_year,
                sy.death_year
            FROM alive_desc ad
            JOIN genealogy.family f
                ON f.tree_id = @tree_id
               AND (f.spouse1_person_id = ad.person_id OR f.spouse2_person_id = ad.person_id)
               AND (f.marriage_year IS NULL OR f.marriage_year <= @year)
            JOIN genealogy.person sp
                ON sp.tree_id = @tree_id
               AND sp.person_id = CASE
                        WHEN f.spouse1_person_id = ad.person_id THEN f.spouse2_person_id
                        ELSE f.spouse1_person_id
                     END
            LEFT JOIN LATERAL (
                SELECT
                    MIN(CASE WHEN e.event_type = 'BIRT' THEN COALESCE(e.year_from, e.year_to) END)::int AS birth_year,
                    MIN(CASE WHEN e.event_type = 'DEAT' THEN COALESCE(e.year_from, e.year_to) END)::int AS death_year
                FROM genealogy.event e
                WHERE e.tree_id = @tree_id
                  AND e.person_id = sp.person_id
                  AND e.event_type IN ('BIRT', 'DEAT')
            ) sy ON true

            ORDER BY generation, person_type DESC, full_name NULLS LAST;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("ancestor_id", NpgsqlDbType.Uuid) { Value = ancestorId });
        command.Parameters.Add(new NpgsqlParameter("year", NpgsqlDbType.Integer) { Value = year });
        command.Parameters.Add(new NpgsqlParameter("max_generations", NpgsqlDbType.Integer) { Value = maxGenerations });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<DescendantAtYearRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DescendantAtYearRow(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                FullName: reader.GetNullableString("full_name"),
                Sex: reader.GetNullableChar("sex"),
                PersonType: reader.GetFieldValue<string>(reader.GetOrdinal("person_type")),
                Generation: reader.GetFieldValue<int>(reader.GetOrdinal("generation")),
                ParentPersonId: reader.GetNullableValue<Guid>("parent_person_id"),
                SpouseOfPersonId: reader.GetNullableValue<Guid>("spouse_of_person_id"),
                BirthYear: reader.GetNullableValue<int>("birth_year"),
                DeathYear: reader.GetNullableValue<int>("death_year")));
        }

        return results;
    }

    /// <summary>
    /// Closest common ancestor across <paramref name="personIds"/>. Each person
    /// is their own ancestor at depth 0; a common ancestor is one reachable from
    /// ALL inputs. The winner is chosen by <c>ORDER BY max_depth, sum_depth,
    /// ancestor_id</c>. Ported from <c>ged.GetClosestCommonAncestorByIds</c>.
    ///
    /// The recursion is reduced to the minimum depth per (person, ancestor)
    /// before aggregating, so <c>sum_depth</c>/<c>max_depth</c> are not inflated
    /// by multiple ancestral paths (a latent defect in the source's raw SUM/MAX)
    /// and each input reports a single, minimal depth to the winner.
    /// </summary>
    public async Task<CommonAncestorResult> GetClosestCommonAncestorAsync(
        Guid treeId,
        IReadOnlyList<Guid> personIds,
        int maxDepth = 12,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(personIds);

        var distinctIds = personIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return new CommonAncestorResult(null, Array.Empty<CommonAncestorInputDepth>());
        }

        // Emits the winning ancestor's columns on every per-input-depth row, so
        // the whole result comes back in one round-trip; `best` is empty (and
        // thus the join yields no rows) when there is no common ancestor.
        const string sql = """
            WITH RECURSIVE ancestors AS (
                SELECT
                    u AS person_id,
                    u AS ancestor_id,
                    0 AS depth,
                    ARRAY[u] AS path
                FROM unnest(@person_ids) AS u

                UNION ALL

                SELECT
                    a.person_id,
                    pc.parent_person_id,
                    a.depth + 1,
                    a.path || pc.parent_person_id
                FROM ancestors a
                JOIN genealogy.parent_child pc
                    ON pc.tree_id = @tree_id
                   AND pc.child_person_id = a.ancestor_id
                WHERE a.depth < @max_depth
                  AND NOT (pc.parent_person_id = ANY(a.path))
            ),
            reachable AS (
                SELECT person_id, ancestor_id, MIN(depth) AS depth
                FROM ancestors
                GROUP BY person_id, ancestor_id
            ),
            common AS (
                SELECT
                    ancestor_id,
                    COUNT(*) AS person_count,
                    MAX(depth) AS max_depth,
                    SUM(depth) AS sum_depth
                FROM reachable
                GROUP BY ancestor_id
                HAVING COUNT(*) = @person_count
            ),
            best AS (
                SELECT ancestor_id, person_count, max_depth, sum_depth
                FROM common
                ORDER BY max_depth, sum_depth, ancestor_id
                LIMIT 1
            )
            SELECT
                b.ancestor_id,
                wp.primary_display_name AS ancestor_name,
                b.max_depth,
                b.sum_depth,
                b.person_count,
                r.person_id,
                pp.primary_display_name AS person_name,
                r.depth
            FROM best b
            JOIN reachable r ON r.ancestor_id = b.ancestor_id
            LEFT JOIN genealogy.person wp
                ON wp.tree_id = @tree_id AND wp.person_id = b.ancestor_id
            LEFT JOIN genealogy.person pp
                ON pp.tree_id = @tree_id AND pp.person_id = r.person_id
            ORDER BY r.depth, r.person_id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = distinctIds });
        command.Parameters.Add(new NpgsqlParameter("person_count", NpgsqlDbType.Integer) { Value = distinctIds.Length });
        command.Parameters.Add(new NpgsqlParameter("max_depth", NpgsqlDbType.Integer) { Value = maxDepth });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        CommonAncestorWinner? winner = null;
        var depths = new List<CommonAncestorInputDepth>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            winner ??= new CommonAncestorWinner(
                AncestorId: reader.GetFieldValue<Guid>(reader.GetOrdinal("ancestor_id")),
                AncestorName: reader.GetNullableString("ancestor_name"),
                MaxDepth: reader.GetFieldValue<int>(reader.GetOrdinal("max_depth")),
                SumDepth: (int)reader.GetFieldValue<long>(reader.GetOrdinal("sum_depth")),
                PersonCount: (int)reader.GetFieldValue<long>(reader.GetOrdinal("person_count")));

            depths.Add(new CommonAncestorInputDepth(
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                PersonName: reader.GetNullableString("person_name"),
                Depth: reader.GetFieldValue<int>(reader.GetOrdinal("depth"))));
        }

        return new CommonAncestorResult(winner, depths);
    }

    /// <summary>
    /// Generic up/down walk from <paramref name="personId"/> returning each
    /// reached node with the edge that produced it and the '&gt;'-joined path
    /// from the root. <paramref name="direction"/> accepts
    /// <c>ancestors</c>/<c>up</c> or <c>descendants</c>/<c>down</c>.
    /// <paramref name="maxGenerations"/> is clamped to [0, 50]. Ported from
    /// <c>ged.GetTreePersonTree</c>, DNA columns dropped.
    ///
    /// Unlike the source (whose seed row was gated on <c>@IncludeRoot = 1</c>,
    /// so <c>@IncludeRoot = 0</c> returned nothing at all), the root is always
    /// seeded to anchor the recursion and is filtered out of the output when
    /// <paramref name="includeRoot"/> is false.
    /// </summary>
    public async Task<IReadOnlyList<PersonTreeNode>> GetPersonTreeAsync(
        Guid treeId,
        Guid personId,
        string direction = "ancestors",
        int maxGenerations = 6,
        bool includeRoot = true,
        CancellationToken cancellationToken = default)
    {
        var mode = NormalizeDirection(direction);
        var clamped = Math.Clamp(maxGenerations, 0, 50);

        // The visited array (seeded with the root) blocks revisiting a node, so
        // a cyclic parent_child graph terminates at the natural frontier; `path`
        // is the human-readable '>'-joined id chain the model exposes.
        const string sql = """
            WITH RECURSIVE walk AS (
                SELECT
                    @person_id::uuid AS person_id,
                    NULL::uuid AS edge_from_person_id,
                    NULL::uuid AS edge_to_person_id,
                    0 AS generation,
                    ARRAY[@person_id::uuid] AS visited,
                    @person_id::text AS path
                UNION ALL
                SELECT
                    CASE WHEN @mode = 'ancestors' THEN pc.parent_person_id ELSE pc.child_person_id END,
                    pc.parent_person_id,
                    pc.child_person_id,
                    w.generation + 1,
                    w.visited || (CASE WHEN @mode = 'ancestors' THEN pc.parent_person_id ELSE pc.child_person_id END),
                    w.path || '>' || (CASE WHEN @mode = 'ancestors' THEN pc.parent_person_id ELSE pc.child_person_id END)::text
                FROM walk w
                JOIN genealogy.parent_child pc
                    ON pc.tree_id = @tree_id
                   AND (
                        (@mode = 'ancestors' AND pc.child_person_id = w.person_id)
                     OR (@mode = 'descendants' AND pc.parent_person_id = w.person_id)
                   )
                WHERE w.generation < @max_generations
                  AND NOT ((CASE WHEN @mode = 'ancestors' THEN pc.parent_person_id ELSE pc.child_person_id END) = ANY(w.visited))
            )
            SELECT
                @person_id::uuid AS root_person_id,
                @mode AS direction,
                w.person_id,
                p.primary_display_name AS full_name,
                p.sex,
                p.is_living,
                w.generation,
                w.edge_from_person_id,
                ef.primary_display_name AS edge_from_name,
                w.edge_to_person_id,
                et.primary_display_name AS edge_to_name,
                w.path,
                birth.year_from::int AS birth_year,
                birth.date_raw AS birth_date_raw,
                bp.place_raw AS birth_place,
                death.year_from::int AS death_year,
                death.date_raw AS death_date_raw,
                dp.place_raw AS death_place
            FROM walk w
            JOIN genealogy.person p
                ON p.tree_id = @tree_id AND p.person_id = w.person_id
            LEFT JOIN genealogy.person ef
                ON ef.tree_id = @tree_id AND ef.person_id = w.edge_from_person_id
            LEFT JOIN genealogy.person et
                ON et.tree_id = @tree_id AND et.person_id = w.edge_to_person_id
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.date_raw, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = @tree_id
                  AND e.person_id = w.person_id
                  AND e.event_type IN ('BIRT', 'CHR')
                ORDER BY CASE WHEN e.event_type = 'BIRT' THEN 0 ELSE 1 END,
                         e.year_from NULLS LAST, e.event_id
                LIMIT 1
            ) birth ON true
            LEFT JOIN LATERAL (
                SELECT e.year_from, e.date_raw, e.place_id
                FROM genealogy.event e
                WHERE e.tree_id = @tree_id
                  AND e.person_id = w.person_id
                  AND e.event_type = 'DEAT'
                ORDER BY e.year_from NULLS LAST, e.event_id
                LIMIT 1
            ) death ON true
            LEFT JOIN genealogy.place bp ON bp.place_id = birth.place_id
            LEFT JOIN genealogy.place dp ON dp.place_id = death.place_id
            WHERE (@include_root OR w.generation > 0)
            ORDER BY w.generation, full_name NULLS LAST, w.path;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("mode", NpgsqlDbType.Text) { Value = mode });
        command.Parameters.Add(new NpgsqlParameter("max_generations", NpgsqlDbType.Integer) { Value = clamped });
        command.Parameters.Add(new NpgsqlParameter("include_root", NpgsqlDbType.Boolean) { Value = includeRoot });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<PersonTreeNode>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new PersonTreeNode(
                RootPersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("root_person_id")),
                Direction: reader.GetFieldValue<string>(reader.GetOrdinal("direction")),
                PersonId: reader.GetFieldValue<Guid>(reader.GetOrdinal("person_id")),
                FullName: reader.GetNullableString("full_name"),
                Sex: reader.GetNullableChar("sex"),
                IsLiving: reader.GetNullableValue<bool>("is_living"),
                Generation: reader.GetFieldValue<int>(reader.GetOrdinal("generation")),
                EdgeFromPersonId: reader.GetNullableValue<Guid>("edge_from_person_id"),
                EdgeFromName: reader.GetNullableString("edge_from_name"),
                EdgeToPersonId: reader.GetNullableValue<Guid>("edge_to_person_id"),
                EdgeToName: reader.GetNullableString("edge_to_name"),
                Path: reader.GetFieldValue<string>(reader.GetOrdinal("path")),
                BirthYear: reader.GetNullableValue<int>("birth_year"),
                BirthDateRaw: reader.GetNullableString("birth_date_raw"),
                BirthPlace: reader.GetNullableString("birth_place"),
                DeathYear: reader.GetNullableValue<int>("death_year"),
                DeathDateRaw: reader.GetNullableString("death_date_raw"),
                DeathPlace: reader.GetNullableString("death_place")));
        }

        return results;
    }

    /// <summary>
    /// Shortest relationship path between <paramref name="personId1"/> and
    /// <paramref name="personId2"/>, exploring <c>parent_child</c> edges in BOTH
    /// directions (parent-&gt;child = <c>PARENT_OF</c>, child-&gt;parent =
    /// <c>CHILD_OF</c>). Ported from the bidirectional-BFS
    /// <c>ged.GetPathBetweenPersonsByName</c>; here implemented as a single
    /// <c>WITH RECURSIVE</c> that carries a <c>uuid[]</c> path (cycle guard:
    /// <c>NOT (next = ANY(path))</c>) plus a parallel relation array, then
    /// reconstructs the shortest reaching path into ordered steps. Returns an
    /// empty list when the two persons are unconnected within
    /// <paramref name="maxDepth"/> hops (or are the same person).
    /// </summary>
    public async Task<IReadOnlyList<RelationshipPathStep>> GetPathBetweenPersonsAsync(
        Guid treeId,
        Guid personId1,
        Guid personId2,
        int maxDepth = 20,
        CancellationToken cancellationToken = default)
    {
        // The recursive frontier enumerates simple paths (no repeated node)
        // outward from person1 up to @max_depth hops; the shortest one that
        // reaches person2 (fewest steps = smallest path cardinality) is picked
        // and expanded into per-hop rows via generate_series over its arrays.
        const string sql = """
            WITH RECURSIVE bfs AS (
                SELECT
                    @person_id1::uuid AS node_id,
                    0 AS depth,
                    ARRAY[@person_id1::uuid] AS path,
                    ARRAY[]::text[] AS rels
                UNION ALL
                SELECT
                    hop.next_id,
                    b.depth + 1,
                    b.path || hop.next_id,
                    b.rels || hop.relation
                FROM bfs b
                JOIN LATERAL (
                    SELECT pc.child_person_id AS next_id, 'PARENT_OF' AS relation
                    FROM genealogy.parent_child pc
                    WHERE pc.tree_id = @tree_id AND pc.parent_person_id = b.node_id
                    UNION ALL
                    SELECT pc.parent_person_id AS next_id, 'CHILD_OF' AS relation
                    FROM genealogy.parent_child pc
                    WHERE pc.tree_id = @tree_id AND pc.child_person_id = b.node_id
                ) hop ON true
                WHERE b.depth < @max_depth
                  AND b.node_id <> @person_id2::uuid
                  AND NOT (hop.next_id = ANY(b.path))
            ),
            target AS (
                SELECT path, rels
                FROM bfs
                WHERE node_id = @person_id2::uuid
                ORDER BY cardinality(path)
                LIMIT 1
            )
            SELECT
                s.step,
                s.from_id,
                fp.primary_display_name AS from_name,
                s.to_id,
                tp.primary_display_name AS to_name,
                s.relation
            FROM target t
            CROSS JOIN LATERAL (
                SELECT
                    gs AS step,
                    t.path[gs] AS from_id,
                    t.path[gs + 1] AS to_id,
                    t.rels[gs] AS relation
                FROM generate_series(1, cardinality(t.path) - 1) AS gs
            ) s
            LEFT JOIN genealogy.person fp
                ON fp.tree_id = @tree_id AND fp.person_id = s.from_id
            LEFT JOIN genealogy.person tp
                ON tp.tree_id = @tree_id AND tp.person_id = s.to_id
            ORDER BY s.step;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
        command.Parameters.Add(new NpgsqlParameter("person_id1", NpgsqlDbType.Uuid) { Value = personId1 });
        command.Parameters.Add(new NpgsqlParameter("person_id2", NpgsqlDbType.Uuid) { Value = personId2 });
        command.Parameters.Add(new NpgsqlParameter("max_depth", NpgsqlDbType.Integer) { Value = maxDepth });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<RelationshipPathStep>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RelationshipPathStep(
                Step: reader.GetFieldValue<int>(reader.GetOrdinal("step")),
                FromId: reader.GetFieldValue<Guid>(reader.GetOrdinal("from_id")),
                FromName: reader.GetNullableString("from_name"),
                ToId: reader.GetFieldValue<Guid>(reader.GetOrdinal("to_id")),
                ToName: reader.GetNullableString("to_name"),
                Relation: reader.GetFieldValue<string>(reader.GetOrdinal("relation"))));
        }

        return results;
    }

    /// <summary>
    /// Normalizes a caller-supplied direction to the canonical <c>ancestors</c>
    /// or <c>descendants</c> the walk SQL expects, mirroring the accepted
    /// spellings of <c>ged.GetTreePersonTree</c>.
    /// </summary>
    private static string NormalizeDirection(string direction)
    {
        var normalized = (direction ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "ancestor" or "ancestors" or "up" => "ancestors",
            "descendant" or "descendants" or "down" => "descendants",
            _ => throw new ArgumentException(
                "Invalid direction. Use 'ancestors'/'up' or 'descendants'/'down'.",
                nameof(direction)),
        };
    }
}
