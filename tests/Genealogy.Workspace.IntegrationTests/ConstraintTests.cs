using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 2 exit criterion: "schema rejects cross-tree relationships and
/// self-links." Exercises the constraints defined in
/// <c>0002_genealogy_core_schema.sql</c> directly via raw parameterized
/// Npgsql commands (so the exact <see cref="PostgresException"/> from the
/// database is observed, with no repository translation in the way), plus
/// the two <c>genealogy.tree</c> unique violations that
/// <see cref="TreeRepository.CreateTreeAsync"/> is expected to translate
/// into typed exceptions.
/// </summary>
public sealed class ConstraintTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    private Guid _treeA;
    private Guid _treeB;
    private Guid _personA1;
    private Guid _personA2;
    private Guid _personB1;

    public ConstraintTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(
            _fixture.BuildOptionsForDatabase(_databaseName));

        new MigrationEngine(_connectionString).Migrate();

        _treeA = Guid.NewGuid();
        _treeB = Guid.NewGuid();
        _personA1 = Guid.NewGuid();
        _personA2 = Guid.NewGuid();
        _personB1 = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeA, "Constraint Tree A");
        await TestSeeding.InsertTreeAsync(connection, _treeB, "Constraint Tree B");
        await TestSeeding.InsertPersonAsync(connection, _treeA, _personA1, primaryDisplayName: "Person A1");
        await TestSeeding.InsertPersonAsync(connection, _treeA, _personA2, primaryDisplayName: "Person A2");
        await TestSeeding.InsertPersonAsync(connection, _treeB, _personB1, primaryDisplayName: "Person B1");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task ParentChild_ChildInDifferentTree_IsRejected()
    {
        // fk_parent_child_child is the composite FK (tree_id, child_person_id)
        // -> person(tree_id, person_id). _personB1 exists, but not under _treeA,
        // so no matching (tree_id, person_id) row exists for the FK to satisfy.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertParentChildAsync(connection, _treeA, _personA1, _personB1));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task ParentChild_SelfParent_IsRejected()
    {
        // ck_parent_child_distinct: check (parent_person_id <> child_person_id).
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertParentChildAsync(connection, _treeA, _personA1, _personA1));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Family_Spouse1EqualsSpouse2_IsRejected()
    {
        // ck_family_distinct_spouses: check (spouse1_person_id <> spouse2_person_id).
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA1, _personA1));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Family_SpouseFromDifferentTree_IsRejected()
    {
        // fk_family_spouse2 is the composite FK (tree_id, spouse2_person_id)
        // -> person(tree_id, person_id); _personB1 belongs to _treeB.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA1, _personB1));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Tree_SecondDefaultTree_IsRejected()
    {
        // uq_tree_one_default: unique index on (is_default) where is_default.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, Guid.NewGuid(), "First Default Tree", isDefault: true);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertTreeAsync(connection, Guid.NewGuid(), "Second Default Tree", isDefault: true));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Person_DuplicateExternalIdInSameTree_IsRejected()
    {
        // uq_person_tree_external_id: partial unique index on (tree_id, external_id)
        // where external_id is not null.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertPersonAsync(connection, _treeA, Guid.NewGuid(), externalId: "EXT-0001");

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertPersonAsync(connection, _treeA, Guid.NewGuid(), externalId: "EXT-0001"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Person_SameExternalIdInDifferentTree_IsAllowed()
    {
        // The partial unique index is scoped by tree_id, so the same
        // external_id may legitimately appear once per tree.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertPersonAsync(connection, _treeA, Guid.NewGuid(), externalId: "SHARED-EXT");
        await TestSeeding.InsertPersonAsync(connection, _treeB, Guid.NewGuid(), externalId: "SHARED-EXT");
        // No exception expected.
    }

    [Fact]
    public async Task Family_DuplicateSpousePairAndYear_IsRejected()
    {
        // uq_family_spouse_pair_year: unique index on
        // (tree_id, least(spouse1,spouse2), greatest(spouse1,spouse2), coalesce(marriage_year,-1)).
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA1, _personA2, marriageYear: 1900);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA1, _personA2, marriageYear: 1900));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Family_DuplicateSpousePairAndYear_SpousesReversed_IsRejected()
    {
        // The unique index uses LEAST/GREATEST, so swapping spouse1/spouse2
        // must still collide with the same (unordered pair, year) tuple.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA1, _personA2, marriageYear: 1905);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA2, _personA1, marriageYear: 1905));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Family_SameSpousePair_DifferentYear_IsAllowed()
    {
        // coalesce(marriage_year, -1) is part of the unique key, so a
        // remarriage-in-a-different-year row for the same couple is legal.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA1, _personA2, marriageYear: 1910);
        await TestSeeding.InsertFamilyAsync(connection, _treeA, Guid.NewGuid(), _personA1, _personA2, marriageYear: 1920);
        // No exception expected.
    }

    [Fact]
    public async Task CreateTreeAsync_DuplicateName_ThrowsDuplicateTreeNameException()
    {
        var repository = new TreeRepository(new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        await repository.CreateTreeAsync("Repo Duplicate Name Tree");

        await Assert.ThrowsAsync<DuplicateTreeNameException>(() =>
            repository.CreateTreeAsync("Repo Duplicate Name Tree"));
    }

    [Fact]
    public async Task CreateTreeAsync_SecondDefaultTree_ThrowsDefaultTreeConflictException()
    {
        var repository = new TreeRepository(new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        await repository.CreateTreeAsync("Repo First Default Tree", isDefault: true);

        await Assert.ThrowsAsync<DefaultTreeConflictException>(() =>
            repository.CreateTreeAsync("Repo Second Default Tree", isDefault: true));
    }
}
