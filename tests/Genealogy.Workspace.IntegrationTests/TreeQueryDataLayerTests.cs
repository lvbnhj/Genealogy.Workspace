using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Resolvers;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 5, Task 2 exit criteria: tree resolver (explicit name, GUID, default
/// fallback, not-found, multiple-match); person resolver (GUID, exact name,
/// multi-match candidates); life-events ordering + related-person name;
/// family-context parity (parents/children/spouses with birth years, siblings,
/// otherParent per child, marriage fields, life-events section); and
/// <c>find_tree_person</c> with father/spouse/year filters.
///
/// One hand-seeded family in a default tree (<c>_treeMain</c>): father +
/// mother + two children, plus a remarriage (father + new partner) mirroring
/// <see cref="FamilyContextTests"/>'s shape but enriched with life events,
/// places and person_name rows so it can also be searched/resolved by name.
/// A second, non-default tree (<c>_treeOther</c>) whose name collides with
/// <c>_treeMain</c>'s only by case exercises the tree resolver's
/// "multiple trees" failure.
/// </summary>
public sealed class TreeQueryDataLayerTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    private TreeResolver _treeResolver = null!;
    private PersonResolver _personResolver = null!;
    private PersonEventsRepository _personEventsRepository = null!;
    private RichFamilyContextRepository _richFamilyContextRepository = null!;
    private PersonSearchRepository _personSearchRepository = null!;

    private Guid _treeMain;
    private Guid _treeOther;

    private Guid _father;
    private Guid _mother;
    private Guid _child1;
    private Guid _child2;
    private Guid _newPartner;

    // Resolver ambiguity fixtures (unrelated to the family above).
    private Guid _johnSmith;
    private Guid _johnSmithy;
    private Guid _ivanKovalenko;
    private Guid _petroKovalenkov;

    public TreeQueryDataLayerTests(WorkspaceEnvironmentFixture fixture)
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

        var connectionFactory = new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName));
        _treeResolver = new TreeResolver(connectionFactory);
        _personResolver = new PersonResolver(connectionFactory);
        _personEventsRepository = new PersonEventsRepository(connectionFactory);
        _richFamilyContextRepository = new RichFamilyContextRepository(connectionFactory);
        _personSearchRepository = new PersonSearchRepository(connectionFactory);

        _treeMain = Guid.NewGuid();
        _treeOther = Guid.NewGuid();

        _father = Guid.NewGuid();
        _mother = Guid.NewGuid();
        _child1 = Guid.NewGuid();
        _child2 = Guid.NewGuid();
        _newPartner = Guid.NewGuid();

        _johnSmith = Guid.NewGuid();
        _johnSmithy = Guid.NewGuid();
        _ivanKovalenko = Guid.NewGuid();
        _petroKovalenkov = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeMain, "Tree Query Data Layer Tree", isDefault: true);
        // Deliberately a *different* name from _treeMain (not just different
        // case) so it doesn't interfere with the "explicit name resolves"
        // test below; the case-collision scenario gets its own dedicated
        // pair of trees in ResolveTreeAsync_NameCollidingByCaseOnly_....
        await TestSeeding.InsertTreeAsync(connection, _treeOther, "Tree Query Data Layer Tree - Other", isDefault: false);

        var lvivBirthPlace = await TestSeeding.InsertPlaceAsync(connection, "Lviv, Austria-Hungary", "lviv");
        var kyivDeathPlace = await TestSeeding.InsertPlaceAsync(connection, "Kyiv, Ukrainian SSR", "kyiv");
        var churchPlace = await TestSeeding.InsertPlaceAsync(connection, "Village Church, Lviv Oblast", "village church");

        await TestSeeding.InsertPersonAsync(connection, _treeMain, _father, primaryDisplayName: "Father Person", sex: 'M', isLiving: false);
        await TestSeeding.InsertPersonAsync(connection, _treeMain, _mother, primaryDisplayName: "Mother Person", sex: 'F', isLiving: false);
        await TestSeeding.InsertPersonAsync(connection, _treeMain, _child1, primaryDisplayName: "Child One", isLiving: true);
        await TestSeeding.InsertPersonAsync(connection, _treeMain, _child2, primaryDisplayName: "Child Two", isLiving: true);
        await TestSeeding.InsertPersonAsync(connection, _treeMain, _newPartner, primaryDisplayName: "New Partner", sex: 'F', isLiving: true);

        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _father, "LAT", "birth",
            "Father Person", "father person", isPrimary: true, given: "Father", surname: "Person");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _mother, "LAT", "birth",
            "Mother Person", "mother person", isPrimary: true, given: "Mother", surname: "Person");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _child1, "LAT", "birth",
            "Child One", "child one", isPrimary: true, given: "Child", surname: "One");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _child2, "LAT", "birth",
            "Child Two", "child two", isPrimary: true, given: "Child", surname: "Two");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _newPartner, "LAT", "birth",
            "New Partner", "new partner", isPrimary: true, given: "New", surname: "Partner");

        // Parents -> children.
        await TestSeeding.InsertParentChildAsync(connection, _treeMain, _father, _child1, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _treeMain, _mother, _child1, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _treeMain, _father, _child2, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _treeMain, _mother, _child2, "BIO");

        // First marriage: father + mother. Second: father's remarriage to the
        // new partner. InsertFamilyAsync's marriageDateRaw/marriagePlaceId
        // params are a Phase 5 extension of the Phase 2 helper.
        var familyFatherMother = Guid.NewGuid();
        var familyFatherNewPartner = Guid.NewGuid();
        await TestSeeding.InsertFamilyAsync(connection, _treeMain, familyFatherMother, _father, _mother,
            marriageYear: 1950, marriageDateRaw: "12 JUN 1950", marriagePlaceId: churchPlace);
        await TestSeeding.InsertFamilyAsync(connection, _treeMain, familyFatherNewPartner, _father, _newPartner,
            marriageYear: 1965);

        // Father's life events: BIRT (1920), MARR (1950, related to mother,
        // tied to the first family), DEAT (1990), and an undated OCCU event
        // that must sort last via coalesce(year_from, 9999).
        await TestSeeding.InsertEventAsync(connection, _treeMain, _father, "BIRT",
            yearFrom: 1920, dateRaw: "1 JAN 1920", dateFrom: new DateOnly(1920, 1, 1), placeId: lvivBirthPlace);
        await TestSeeding.InsertEventAsync(connection, _treeMain, _father, "MARR",
            yearFrom: 1950, dateRaw: "12 JUN 1950", familyId: familyFatherMother, relatedPersonId: _mother, placeId: churchPlace);
        await TestSeeding.InsertEventAsync(connection, _treeMain, _father, "DEAT",
            yearFrom: 1990, dateRaw: "3 MAR 1990", placeId: kyivDeathPlace);
        await TestSeeding.InsertEventAsync(connection, _treeMain, _father, "OCCU",
            eventValue: "Farmer");

        await TestSeeding.InsertEventAsync(connection, _treeMain, _mother, "BIRT", yearFrom: 1922, placeId: lvivBirthPlace);
        await TestSeeding.InsertEventAsync(connection, _treeMain, _mother, "DEAT", yearFrom: 1995, placeId: kyivDeathPlace);

        // Same-year BIRT/BAPT pair on child1 to prove the event-type rank
        // tiebreak (BIRT=1 before BAPT=2) applies when years are equal.
        await TestSeeding.InsertEventAsync(connection, _treeMain, _child1, "BAPT", yearFrom: 1975, dateRaw: "baptism 1975");
        await TestSeeding.InsertEventAsync(connection, _treeMain, _child1, "BIRT", yearFrom: 1975, dateRaw: "birth 1975", placeId: lvivBirthPlace);

        await TestSeeding.InsertEventAsync(connection, _treeMain, _child2, "BIRT", yearFrom: 1978, placeId: lvivBirthPlace);

        // Person-resolver fixtures: one pair with a unique exact match amid a
        // non-exact substring collision, one pair with no exact match at all
        // (genuine ambiguity).
        await TestSeeding.InsertPersonAsync(connection, _treeMain, _johnSmith, primaryDisplayName: "John Smith");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _johnSmith, "LAT", "birth",
            "John Smith", "john smith", isPrimary: true, given: "John", surname: "Smith");
        await TestSeeding.InsertPersonAsync(connection, _treeMain, _johnSmithy, primaryDisplayName: "John Smithy");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _johnSmithy, "LAT", "birth",
            "John Smithy", "john smithy", isPrimary: true, given: "John", surname: "Smithy");

        await TestSeeding.InsertPersonAsync(connection, _treeMain, _ivanKovalenko, primaryDisplayName: "Ivan Kovalenko");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _ivanKovalenko, "LAT", "birth",
            "Ivan Kovalenko", "ivan kovalenko", isPrimary: true, given: "Ivan", surname: "Kovalenko");
        await TestSeeding.InsertPersonAsync(connection, _treeMain, _petroKovalenkov, primaryDisplayName: "Petro Kovalenkov");
        await TestSeeding.InsertPersonNameAsync(connection, _treeMain, _petroKovalenkov, "LAT", "birth",
            "Petro Kovalenkov", "petro kovalenkov", isPrimary: true, given: "Petro", surname: "Kovalenkov");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    // ------------------------------------------------------------------
    // TreeResolver
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveTreeAsync_ExplicitName_Resolves()
    {
        var result = await _treeResolver.ResolveTreeAsync("Tree Query Data Layer Tree");

        Assert.True(result.IsResolved);
        Assert.Equal(_treeMain, result.TreeId);
        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task ResolveTreeAsync_ExplicitGuid_Resolves()
    {
        var result = await _treeResolver.ResolveTreeAsync(_treeMain.ToString());

        Assert.True(result.IsResolved);
        Assert.Equal(_treeMain, result.TreeId);
    }

    [Fact]
    public async Task ResolveTreeAsync_NullInput_FallsBackToDefault()
    {
        var result = await _treeResolver.ResolveTreeAsync(null);

        Assert.True(result.IsResolved);
        Assert.Equal(_treeMain, result.TreeId);
        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task ResolveTreeAsync_UnknownName_NotResolved_WithNotFoundReason()
    {
        var result = await _treeResolver.ResolveTreeAsync("No Such Tree");

        Assert.False(result.IsResolved);
        Assert.Contains("not found", result.FailureReason);
    }

    [Fact]
    public async Task ResolveTreeAsync_UnknownGuid_NotResolved_WithNotFoundReason()
    {
        var result = await _treeResolver.ResolveTreeAsync(Guid.NewGuid().ToString());

        Assert.False(result.IsResolved);
        Assert.Contains("not found", result.FailureReason);
    }

    [Fact]
    public async Task ResolveTreeAsync_NameCollidingByCaseOnly_NotResolved_WithMultipleReason()
    {
        // genealogy.tree.name is unique case-sensitively, so two trees whose
        // names differ only by case can coexist; the resolver's
        // case-insensitive match must find both and refuse to silently pick
        // one, rather than the first row postgres happens to return.
        var collisionA = Guid.NewGuid();
        var collisionB = Guid.NewGuid();

        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await TestSeeding.InsertTreeAsync(connection, collisionA, "Case Collision Tree");
            await TestSeeding.InsertTreeAsync(connection, collisionB, "case collision tree");
        }

        var result = await _treeResolver.ResolveTreeAsync("CASE COLLISION TREE");

        Assert.False(result.IsResolved);
        Assert.Contains("multiple trees", result.FailureReason);
    }

    [Fact]
    public async Task ResolveTreeAsync_NoDefaultTree_NotResolved_WithNoDefaultReason()
    {
        // A fresh database with zero trees at all has no default either.
        var emptyDatabaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(emptyDatabaseName);
        try
        {
            var emptyConnectionString = NpgsqlConnectionFactory.BuildConnectionString(
                _fixture.BuildOptionsForDatabase(emptyDatabaseName));
            new MigrationEngine(emptyConnectionString).Migrate();

            var resolver = new TreeResolver(new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(emptyDatabaseName)));
            var result = await resolver.ResolveTreeAsync(null);

            Assert.False(result.IsResolved);
            Assert.Equal("no default tree", result.FailureReason);
        }
        finally
        {
            await _fixture.DropDatabaseAsync(emptyDatabaseName);
        }
    }

    // ------------------------------------------------------------------
    // PersonResolver
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolvePersonAsync_Guid_Resolves_AndVerifiesTreeMembership()
    {
        var result = await _personResolver.ResolvePersonAsync(_treeMain, _father.ToString());

        Assert.True(result.IsResolved);
        Assert.Equal(_father, result.PersonId);
        Assert.Equal("Father Person", result.FullName);
    }

    [Fact]
    public async Task ResolvePersonAsync_GuidFromOtherTree_NotFound()
    {
        // _father only exists under _treeMain; asking under _treeOther must
        // not leak across trees.
        var result = await _personResolver.ResolvePersonAsync(_treeOther, _father.ToString());

        Assert.False(result.IsResolved);
        Assert.Contains("not found", result.FailureReason);
    }

    [Fact]
    public async Task ResolvePersonAsync_UniqueExactMatch_ResolvesDespiteNonExactSubstringCollision()
    {
        // "John Smith" is an exact match for _johnSmith; "John Smithy" only
        // matches as a substring. A unique exact match must still resolve
        // automatically.
        var result = await _personResolver.ResolvePersonAsync(_treeMain, "John Smith");

        Assert.True(result.IsResolved);
        Assert.Equal(_johnSmith, result.PersonId);
    }

    [Fact]
    public async Task ResolvePersonAsync_AmbiguousSubstring_NoExactMatch_ReturnsCandidates()
    {
        // "Kovale" matches both "Ivan Kovalenko" and "Petro Kovalenkov" as a
        // substring, and is an exact match for neither: must not silently
        // pick, must return both as candidates.
        var result = await _personResolver.ResolvePersonAsync(_treeMain, "Kovale");

        Assert.False(result.IsResolved);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.PersonId == _ivanKovalenko);
        Assert.Contains(result.Candidates, c => c.PersonId == _petroKovalenkov);
    }

    [Fact]
    public async Task ResolvePersonAsync_NoMatch_NotFound()
    {
        var result = await _personResolver.ResolvePersonAsync(_treeMain, "Zzzznomatch");

        Assert.False(result.IsResolved);
        Assert.Contains("not found", result.FailureReason);
    }

    // ------------------------------------------------------------------
    // PersonEventsRepository
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetLifeEventsAsync_Father_OrdersEventsChronologically_UndatedEventLast()
    {
        var result = await _personEventsRepository.GetLifeEventsAsync(_treeMain, _father);

        Assert.NotNull(result);
        Assert.Equal("Father Person", result!.Header.FullName);
        Assert.Equal('M', result.Header.Sex);

        var types = result.Events.Select(e => e.EventType).ToList();
        Assert.Equal(new[] { "BIRT", "MARR", "DEAT", "OCCU" }, types);
    }

    [Fact]
    public async Task GetLifeEventsAsync_Father_MarriageEvent_ResolvesRelatedPersonName()
    {
        var result = await _personEventsRepository.GetLifeEventsAsync(_treeMain, _father);

        var marriage = Assert.Single(result!.Events, e => e.EventType == "MARR");
        Assert.Equal(_mother, marriage.RelatedPersonId);
        Assert.Equal("Mother Person", marriage.RelatedPersonName);
        Assert.Equal("Village Church, Lviv Oblast", marriage.PlaceRaw);
    }

    [Fact]
    public async Task GetLifeEventsAsync_Child_SameYearBirtAndBapt_BirtOrderedFirst()
    {
        var result = await _personEventsRepository.GetLifeEventsAsync(_treeMain, _child1);

        var types = result!.Events.Select(e => e.EventType).ToList();
        Assert.Equal(new[] { "BIRT", "BAPT" }, types);
    }

    [Fact]
    public async Task GetLifeEventsAsync_UnknownPerson_ReturnsNull()
    {
        var result = await _personEventsRepository.GetLifeEventsAsync(_treeMain, Guid.NewGuid());

        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // RichFamilyContextRepository (parity with ged.GetPersonFamilyContext)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetRichFamilyContextAsync_Father_HasLifeEventsSection()
    {
        var context = await _richFamilyContextRepository.GetRichFamilyContextAsync(_treeMain, _father);

        Assert.NotNull(context);
        Assert.Equal("Father Person", context!.PrimaryDisplayName);
        Assert.Equal(4, context.LifeEvents.Count);
        Assert.Equal("BIRT", context.LifeEvents[0].EventType);
    }

    [Fact]
    public async Task GetRichFamilyContextAsync_Child_HasBothParents_WithBirthAndDeathYears()
    {
        var context = await _richFamilyContextRepository.GetRichFamilyContextAsync(_treeMain, _child1);

        Assert.Equal(2, context!.Parents.Count);
        var father = Assert.Single(context.Parents, p => p.PersonId == _father);
        Assert.Equal((short)1920, father.BirthYear);
        Assert.Equal("Lviv, Austria-Hungary", father.BirthPlace);
        Assert.Equal((short)1990, father.DeathYear);
        Assert.Equal("Kyiv, Ukrainian SSR", father.DeathPlace);
    }

    [Fact]
    public async Task GetRichFamilyContextAsync_Child_HasSibling()
    {
        var context = await _richFamilyContextRepository.GetRichFamilyContextAsync(_treeMain, _child1);

        var sibling = Assert.Single(context!.Siblings);
        Assert.Equal(_child2, sibling.PersonId);
        Assert.Equal((short)1978, sibling.BirthYear);
    }

    [Fact]
    public async Task GetRichFamilyContextAsync_Father_HasTwoMarriages_WithMarriageFields()
    {
        var context = await _richFamilyContextRepository.GetRichFamilyContextAsync(_treeMain, _father);

        Assert.Equal(2, context!.Marriages.Count);

        var toMother = Assert.Single(context.Marriages, m => m.SpousePersonId == _mother);
        Assert.Equal((short)1950, toMother.MarriageYear);
        Assert.Equal("12 JUN 1950", toMother.MarriageDateRaw);
        Assert.Equal("Village Church, Lviv Oblast", toMother.MarriagePlaceRaw);

        var toNewPartner = Assert.Single(context.Marriages, m => m.SpousePersonId == _newPartner);
        Assert.Equal((short)1965, toNewPartner.MarriageYear);
    }

    [Fact]
    public async Task GetRichFamilyContextAsync_Father_Children_HaveOtherParent()
    {
        var context = await _richFamilyContextRepository.GetRichFamilyContextAsync(_treeMain, _father);

        Assert.Equal(2, context!.Children.Count);
        Assert.All(context.Children, c =>
        {
            Assert.Equal("Mother Person", c.OtherParentName);
            Assert.Equal('F', c.OtherParentSex);
        });
    }

    [Fact]
    public async Task GetRichFamilyContextAsync_LonePerson_ReturnsEmptyListsNotNull()
    {
        var context = await _richFamilyContextRepository.GetRichFamilyContextAsync(_treeMain, _johnSmith);

        Assert.NotNull(context);
        Assert.Empty(context!.Parents);
        Assert.Empty(context.Siblings);
        Assert.Empty(context.Marriages);
        Assert.Empty(context.Children);
        Assert.Empty(context.LifeEvents);
    }

    [Fact]
    public async Task GetRichFamilyContextAsync_UnknownPerson_ReturnsNull()
    {
        var context = await _richFamilyContextRepository.GetRichFamilyContextAsync(_treeMain, Guid.NewGuid());

        Assert.Null(context);
    }

    // ------------------------------------------------------------------
    // PersonSearchRepository.FindTreePersonAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task FindTreePersonAsync_FatherFilter_ReturnsBothChildren()
    {
        var results = await _personSearchRepository.FindTreePersonAsync(
            _treeMain, name: "Child", father: "Father Person");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.PersonId == _child1);
        Assert.Contains(results, r => r.PersonId == _child2);
    }

    [Fact]
    public async Task FindTreePersonAsync_SpouseFilter_ReturnsOnlyMatchingSpousePair()
    {
        var results = await _personSearchRepository.FindTreePersonAsync(
            _treeMain, name: "Father", spouse: "New Partner");

        var result = Assert.Single(results);
        Assert.Equal(_father, result.PersonId);
    }

    [Fact]
    public async Task FindTreePersonAsync_YearRange_FiltersToMatchingChild()
    {
        var results = await _personSearchRepository.FindTreePersonAsync(
            _treeMain, name: "Child", yearFrom: 1974, yearTo: 1976);

        var result = Assert.Single(results);
        Assert.Equal(_child1, result.PersonId);
        Assert.Equal((short)1975, result.BirthYear);
    }

    [Fact]
    public async Task FindTreePersonAsync_ScopedToTree_DoesNotLeakAcrossTrees()
    {
        var results = await _personSearchRepository.FindTreePersonAsync(_treeOther, name: "Father");

        Assert.Empty(results);
    }
}
