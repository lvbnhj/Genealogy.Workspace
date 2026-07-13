using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 2 exit criterion: "core query integration tests pass." Exercises
/// <see cref="FamilyContextRepository.GetFamilyContextAsync"/> against a small
/// hand-built family: two parents, two children (via
/// <c>genealogy.parent_child</c>), a <c>genealogy.family</c> row linking the
/// parents as spouses, and a remarriage (one parent is also a spouse in a
/// second family).
/// </summary>
public sealed class FamilyContextTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private FamilyContextRepository _familyContextRepository = null!;

    private Guid _treeA;
    private Guid _treeB;

    private Guid _father;
    private Guid _mother;
    private Guid _child1;
    private Guid _child2;
    private Guid _newPartner;
    private Guid _loner;
    private Guid _familyFatherMother;
    private Guid _familyFatherNewPartner;
    private Guid _personOnlyInTreeB;

    public FamilyContextTests(WorkspaceEnvironmentFixture fixture)
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

        _familyContextRepository = new FamilyContextRepository(
            new NpgsqlConnectionFactory(_fixture.BuildOptionsForDatabase(_databaseName)));

        _treeA = Guid.NewGuid();
        _treeB = Guid.NewGuid();
        _father = Guid.NewGuid();
        _mother = Guid.NewGuid();
        _child1 = Guid.NewGuid();
        _child2 = Guid.NewGuid();
        _newPartner = Guid.NewGuid();
        _loner = Guid.NewGuid();
        _familyFatherMother = Guid.NewGuid();
        _familyFatherNewPartner = Guid.NewGuid();
        _personOnlyInTreeB = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeA, "Family Context Tree A");
        await TestSeeding.InsertTreeAsync(connection, _treeB, "Family Context Tree B");

        await TestSeeding.InsertPersonAsync(connection, _treeA, _father, primaryDisplayName: "Father Person", sex: 'M');
        await TestSeeding.InsertPersonAsync(connection, _treeA, _mother, primaryDisplayName: "Mother Person", sex: 'F');
        await TestSeeding.InsertPersonAsync(connection, _treeA, _child1, primaryDisplayName: "Child One");
        await TestSeeding.InsertPersonAsync(connection, _treeA, _child2, primaryDisplayName: "Child Two");
        await TestSeeding.InsertPersonAsync(connection, _treeA, _newPartner, primaryDisplayName: "New Partner", sex: 'F');
        await TestSeeding.InsertPersonAsync(connection, _treeA, _loner, primaryDisplayName: "Lone Person");
        await TestSeeding.InsertPersonAsync(connection, _treeB, _personOnlyInTreeB, primaryDisplayName: "Only In Tree B");

        // Parents -> children.
        await TestSeeding.InsertParentChildAsync(connection, _treeA, _father, _child1, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _treeA, _mother, _child1, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _treeA, _father, _child2, "BIO");
        await TestSeeding.InsertParentChildAsync(connection, _treeA, _mother, _child2, "BIO");

        // First marriage: father + mother.
        await TestSeeding.InsertFamilyAsync(connection, _treeA, _familyFatherMother, _father, _mother, marriageYear: 1950);

        // Remarriage: father + new partner, in a second family.
        await TestSeeding.InsertFamilyAsync(connection, _treeA, _familyFatherNewPartner, _father, _newPartner, marriageYear: 1965);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task GetFamilyContextAsync_Child_HasBothParents_NoChildrenOrSpouses()
    {
        var context = await _familyContextRepository.GetFamilyContextAsync(_treeA, _child1);

        Assert.Equal(2, context.Parents.Count);
        Assert.Contains(context.Parents, p => p.PersonId == _father);
        Assert.Contains(context.Parents, p => p.PersonId == _mother);
        Assert.Empty(context.Children);
        Assert.Empty(context.Spouses);
    }

    [Fact]
    public async Task GetFamilyContextAsync_Father_HasBothChildren_NoParents()
    {
        var context = await _familyContextRepository.GetFamilyContextAsync(_treeA, _father);

        Assert.Equal(2, context.Children.Count);
        Assert.Contains(context.Children, c => c.PersonId == _child1);
        Assert.Contains(context.Children, c => c.PersonId == _child2);
        Assert.Empty(context.Parents);
    }

    [Fact]
    public async Task GetFamilyContextAsync_Father_HasTwoSpouses_FromRemarriage()
    {
        var context = await _familyContextRepository.GetFamilyContextAsync(_treeA, _father);

        Assert.Equal(2, context.Spouses.Count);
        Assert.Contains(context.Spouses, s => s.PersonId == _mother && s.MarriageYear == 1950);
        Assert.Contains(context.Spouses, s => s.PersonId == _newPartner && s.MarriageYear == 1965);
    }

    [Fact]
    public async Task GetFamilyContextAsync_Mother_DerivesSpouseWhenPersonIsSpouse1_FatherIsSpouse2Side()
    {
        // family row stores father as spouse1 and mother as spouse2 (see
        // InsertFamilyAsync call order above); querying from the mother's
        // side must still resolve the father as the "other" spouse.
        var context = await _familyContextRepository.GetFamilyContextAsync(_treeA, _mother);

        Assert.Single(context.Spouses);
        Assert.Equal(_father, context.Spouses[0].PersonId);
        Assert.Equal((short)1950, context.Spouses[0].MarriageYear);
    }

    [Fact]
    public async Task GetFamilyContextAsync_LonePerson_ReturnsEmptyListsNotNull()
    {
        var context = await _familyContextRepository.GetFamilyContextAsync(_treeA, _loner);

        Assert.NotNull(context.Parents);
        Assert.NotNull(context.Children);
        Assert.NotNull(context.Spouses);
        Assert.Empty(context.Parents);
        Assert.Empty(context.Children);
        Assert.Empty(context.Spouses);
    }

    [Fact]
    public async Task GetFamilyContextAsync_PersonOnlyInDifferentTree_ReturnsEmptyContext()
    {
        // _personOnlyInTreeB was never inserted under _treeA, so asking for
        // their family context scoped to _treeA must come back empty rather
        // than leaking their (nonexistent, under _treeA) relations.
        var context = await _familyContextRepository.GetFamilyContextAsync(_treeA, _personOnlyInTreeB);

        Assert.Empty(context.Parents);
        Assert.Empty(context.Children);
        Assert.Empty(context.Spouses);
    }
}
