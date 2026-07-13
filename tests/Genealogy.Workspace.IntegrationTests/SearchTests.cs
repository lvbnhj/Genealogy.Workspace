using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 2 exit criterion: "core query integration tests pass with Cyrillic
/// AND Latin names." Exercises <see cref="PersonRepository.SearchPersonsByNameAsync"/>
/// against <c>full_name</c>/<c>full_name_normalized</c> values inserted
/// directly (name normalization is a Phase 3 concern; here the normalized
/// column is set explicitly, exactly as a Phase 3 writer would).
/// </summary>
public sealed class SearchTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private PersonRepository _personRepository = null!;

    private Guid _treeA;
    private Guid _treeB;

    private Guid _ivan;
    private Guid _klementina;
    private Guid _jan;
    private Guid _percentDecoy;
    private Guid _percentTarget;
    private Guid _underscoreDecoy;
    private Guid _underscoreTarget;
    private Guid _backslashDecoy;
    private Guid _backslashTarget;
    private Guid _ivanInTreeB;

    public SearchTests(WorkspaceEnvironmentFixture fixture)
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

        _personRepository = new PersonRepository(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        _treeA = Guid.NewGuid();
        _treeB = Guid.NewGuid();

        _ivan = Guid.NewGuid();
        _klementina = Guid.NewGuid();
        _jan = Guid.NewGuid();
        _percentDecoy = Guid.NewGuid();
        _percentTarget = Guid.NewGuid();
        _underscoreDecoy = Guid.NewGuid();
        _underscoreTarget = Guid.NewGuid();
        _backslashDecoy = Guid.NewGuid();
        _backslashTarget = Guid.NewGuid();
        _ivanInTreeB = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeA, "Search Tree A");
        await TestSeeding.InsertTreeAsync(connection, _treeB, "Search Tree B");

        // Cyrillic person, single primary name.
        await TestSeeding.InsertPersonAsync(connection, _treeA, _ivan, primaryDisplayName: "Іван Тестенко", surnameNormalized: "тестенко");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _ivan, scriptCode: "CYR", nameType: "birth",
            fullName: "Іван Тестенко", fullNameNormalized: "іван тестенко",
            isPrimary: true, given: "Іван", surname: "тестенко");

        // Cyrillic person with TWO person_name rows sharing the same surname,
        // to prove SearchPersonsByNameAsync's DISTINCT collapses the join.
        await TestSeeding.InsertPersonAsync(connection, _treeA, _klementina, primaryDisplayName: "Клементіна Семашко", surnameNormalized: "семашко");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _klementina, scriptCode: "CYR", nameType: "birth",
            fullName: "Клементіна Семашко", fullNameNormalized: "клементіна семашко",
            isPrimary: true, given: "Клементіна", surname: "семашко");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _klementina, scriptCode: "CYR", nameType: "alias",
            fullName: "Клема Семашко", fullNameNormalized: "клема семашко",
            isPrimary: false, given: "Клема", surname: "семашко");

        // Latin person.
        await TestSeeding.InsertPersonAsync(connection, _treeA, _jan, primaryDisplayName: "Jan Kowalski", surnameNormalized: "kowalski");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _jan, scriptCode: "LAT", nameType: "birth",
            fullName: "Jan Kowalski", fullNameNormalized: "jan kowalski",
            isPrimary: true, given: "Jan", surname: "kowalski");

        // '%' escaping pair: decoy matches the *wildcard* interpretation of
        // "jan%smith" (jan ... smith) but not the literal substring "jan%smith".
        await TestSeeding.InsertPersonAsync(connection, _treeA, _percentDecoy, primaryDisplayName: "Percent Decoy");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _percentDecoy, scriptCode: "LAT", nameType: "birth",
            fullName: "Jan Additional Something Smith", fullNameNormalized: "jan additional something smith",
            isPrimary: true);
        await TestSeeding.InsertPersonAsync(connection, _treeA, _percentTarget, primaryDisplayName: "Percent Target");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _percentTarget, scriptCode: "LAT", nameType: "birth",
            fullName: "Weird Jan%Smith Person", fullNameNormalized: "weird jan%smith person",
            isPrimary: true);

        // '_' escaping pair: decoy matches the *wildcard* interpretation of
        // "jan_smith" (jan + any single char + smith) but has no literal underscore.
        await TestSeeding.InsertPersonAsync(connection, _treeA, _underscoreDecoy, primaryDisplayName: "Underscore Decoy");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _underscoreDecoy, scriptCode: "LAT", nameType: "birth",
            fullName: "The Janxsmith Family", fullNameNormalized: "the janxsmith family",
            isPrimary: true);
        await TestSeeding.InsertPersonAsync(connection, _treeA, _underscoreTarget, primaryDisplayName: "Underscore Target");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _underscoreTarget, scriptCode: "LAT", nameType: "birth",
            fullName: "The Jan_Smith Family", fullNameNormalized: "the jan_smith family",
            isPrimary: true);

        // '\' escaping pair: if the query's backslash were left unescaped, it
        // would itself act as the LIKE escape character and consume the next
        // character, making "jan\smith" match a name with no backslash at all
        // (e.g. "jansmith"). Correct escaping requires a literal backslash.
        await TestSeeding.InsertPersonAsync(connection, _treeA, _backslashDecoy, primaryDisplayName: "Backslash Decoy");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _backslashDecoy, scriptCode: "LAT", nameType: "birth",
            fullName: "The Jansmith Family", fullNameNormalized: "the jansmith family",
            isPrimary: true);
        await TestSeeding.InsertPersonAsync(connection, _treeA, _backslashTarget, primaryDisplayName: "Backslash Target");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeA, _backslashTarget, scriptCode: "LAT", nameType: "birth",
            fullName: "The Jan\\Smith Family", fullNameNormalized: "the jan\\smith family",
            isPrimary: true);

        // Same normalized name as _ivan, but in a different tree: proves
        // search results never leak across tree_id.
        await TestSeeding.InsertPersonAsync(connection, _treeB, _ivanInTreeB, primaryDisplayName: "Іван Тестенко (Tree B)", surnameNormalized: "тестенко");
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeB, _ivanInTreeB, scriptCode: "CYR", nameType: "birth",
            fullName: "Іван Тестенко", fullNameNormalized: "іван тестенко",
            isPrimary: true, given: "Іван", surname: "тестенко");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_CyrillicQuery_MatchesCyrillicName()
    {
        var results = await _personRepository.SearchPersonsByNameAsync(_treeA, "іван тестенко", 50);

        Assert.Contains(results, p => p.PersonId == _ivan);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_LatinQuery_MatchesLatinName()
    {
        var results = await _personRepository.SearchPersonsByNameAsync(_treeA, "kowalski", 50);

        Assert.Contains(results, p => p.PersonId == _jan);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_SubstringQuery_MatchesMidWord()
    {
        // "естенк" is a mid-word substring of "тестенко" — proves the match
        // is a substring (ILIKE %...%), not a prefix or whole-word match.
        var results = await _personRepository.SearchPersonsByNameAsync(_treeA, "естенк", 50);

        Assert.Contains(results, p => p.PersonId == _ivan);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_PersonWithTwoNameRows_AppearsOnce()
    {
        var results = await _personRepository.SearchPersonsByNameAsync(_treeA, "семашко", 50);

        Assert.Single(results, p => p.PersonId == _klementina);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_PercentInQuery_DoesNotActAsWildcard()
    {
        var results = await _personRepository.SearchPersonsByNameAsync(_treeA, "jan%smith", 50);

        Assert.Contains(results, p => p.PersonId == _percentTarget);
        Assert.DoesNotContain(results, p => p.PersonId == _percentDecoy);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_UnderscoreInQuery_DoesNotActAsWildcard()
    {
        var results = await _personRepository.SearchPersonsByNameAsync(_treeA, "jan_smith", 50);

        Assert.Contains(results, p => p.PersonId == _underscoreTarget);
        Assert.DoesNotContain(results, p => p.PersonId == _underscoreDecoy);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_BackslashInQuery_DoesNotActAsWildcard()
    {
        var results = await _personRepository.SearchPersonsByNameAsync(_treeA, "jan\\smith", 50);

        Assert.Contains(results, p => p.PersonId == _backslashTarget);
        Assert.DoesNotContain(results, p => p.PersonId == _backslashDecoy);
    }

    [Fact]
    public async Task SearchPersonsByNameAsync_ResultsDoNotLeakAcrossTrees()
    {
        var resultsInTreeA = await _personRepository.SearchPersonsByNameAsync(_treeA, "тестенко", 50);
        var resultsInTreeB = await _personRepository.SearchPersonsByNameAsync(_treeB, "тестенко", 50);

        Assert.Contains(resultsInTreeA, p => p.PersonId == _ivan);
        Assert.DoesNotContain(resultsInTreeA, p => p.PersonId == _ivanInTreeB);
        Assert.All(resultsInTreeA, p => Assert.Equal(_treeA, p.TreeId));

        Assert.Contains(resultsInTreeB, p => p.PersonId == _ivanInTreeB);
        Assert.DoesNotContain(resultsInTreeB, p => p.PersonId == _ivan);
        Assert.All(resultsInTreeB, p => Assert.Equal(_treeB, p.TreeId));
    }
}
