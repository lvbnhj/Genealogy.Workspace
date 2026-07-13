using System.Linq;
using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Research;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 6, Task 3 exit criteria for the Evidence Inbox record/mention/link/
/// search services (<c>SourceRecordRepository</c>, <c>RecordMentionRepository</c>,
/// <c>PersonLinkService</c>, <c>SourceRecordSearchRepository</c>), all layered
/// over the schema from migration 0009_research_evidence_schema.sql. The
/// fixture mirrors <see cref="ResearchSchemaTests"/>: a fresh, uniquely named
/// database per test, migrations applied by <see cref="MigrationEngine"/>,
/// then one tree with two <c>genealogy.person</c> rows — one surnamed like
/// the evidence mention ("Семашко") so <see cref="PersonLinkService.SuggestLinksAsync"/>
/// has something to find, one unrelated ("Бондар").
/// </summary>
public sealed class EvidenceRecordTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private NpgsqlConnectionFactory _connectionFactory = null!;

    private Guid _treeId;
    private Guid _semashkoPersonId;
    private Guid _bondarPersonId;

    public EvidenceRecordTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);

        var options = _fixture.BuildOptionsForDatabase(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(options);
        _connectionFactory = new NpgsqlConnectionFactory(options);

        new MigrationEngine(_connectionString).Migrate();

        _treeId = Guid.NewGuid();
        _semashkoPersonId = Guid.NewGuid();
        _bondarPersonId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await TestSeeding.InsertTreeAsync(connection, _treeId, "Evidence Inbox Tree");

        // Matching person: surname "Семашко" (lowercase-normalized, as the
        // schema's convention prescribes), birth year 1850 — close to the
        // mention's estimated birth year so the scorer picks up both signals.
        await TestSeeding.InsertPersonAsync(
            connection, _treeId, _semashkoPersonId,
            primaryDisplayName: "Іван Семашко", surnameNormalized: "семашко", sex: 'M');
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeId, _semashkoPersonId, scriptCode: "cyrl", nameType: "birth",
            fullName: "Іван Семашко", fullNameNormalized: "іван семашко", isPrimary: true,
            given: "Іван", surname: "Семашко");
        await TestSeeding.InsertEventAsync(
            connection, _treeId, _semashkoPersonId, eventType: "BIRT", yearFrom: 1850);

        // Unrelated person: surname "Бондар", no matching signal expected
        // against the "Семашко" mention.
        await TestSeeding.InsertPersonAsync(
            connection, _treeId, _bondarPersonId,
            primaryDisplayName: "Петро Бондар", surnameNormalized: "бондар", sex: 'M');
        await TestSeeding.InsertPersonNameAsync(
            connection, _treeId, _bondarPersonId, scriptCode: "cyrl", nameType: "birth",
            fullName: "Петро Бондар", fullNameNormalized: "петро бондар", isPrimary: true,
            given: "Петро", surname: "Бондар");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task EvidenceInboxLifecycle_RecordMentionSearchSuggestAcceptReject()
    {
        var sourceRecords = new SourceRecordRepository(_connectionFactory);
        var mentions = new RecordMentionRepository(_connectionFactory);
        var search = new SourceRecordSearchRepository(_connectionFactory);
        var links = new PersonLinkService(_connectionFactory);

        // (1) AddRecordAsync -> GetRecordAsync round-trips fields, status 'inbox'.
        var recordInput = new SourceRecordInput(
            TreeId: _treeId,
            Title: "Metric book: births, village X, 1850",
            RecordType: "birth",
            RecordYearFrom: 1850,
            RecordYearTo: 1850,
            PlaceText: "Village X",
            ArchiveName: "TsDIAK",
            Fond: "127",
            Opis: "1015",
            Sprava: "42");

        var created = await sourceRecords.AddRecordAsync(recordInput);
        Assert.Equal("inbox", created.Status);

        var detail = await sourceRecords.GetRecordAsync(created.SourceRecordId);
        Assert.NotNull(detail);
        Assert.Equal(created.SourceRecordId, detail!.SourceRecordId);
        Assert.Equal(_treeId, detail.TreeId);
        Assert.Equal("Metric book: births, village X, 1850", detail.Title);
        Assert.Equal("birth", detail.RecordType);
        Assert.Equal("inbox", detail.Status);
        Assert.Equal((short)1850, detail.RecordYearFrom);
        Assert.Equal("TsDIAK", detail.ArchiveName);
        Assert.Empty(detail.PersonMentions);
        Assert.Empty(detail.PlaceMentions);
        Assert.Empty(detail.Keywords);

        // A record that does not exist returns null.
        Assert.Null(await sourceRecords.GetRecordAsync(Guid.NewGuid()));

        // (2) Add a person mention (surname 'Семашко') + place mention + keyword.
        var mentionInput = new PersonMentionInput(
            NameText: "Іван Семашко",
            GivenName: "Іван",
            Surname: "Семашко",
            Patronymic: null,
            Sex: 'M',
            Role: "child",
            AgeText: null,
            EstimatedBirthYear: 1850,
            SocialStatus: null,
            RelationshipText: null);
        var mentionResult = await mentions.AddPersonMentionAsync(created.SourceRecordId, mentionInput);
        Assert.Equal(created.SourceRecordId, mentionResult.SourceRecordId);

        await mentions.AddPlaceMentionAsync(
            created.SourceRecordId,
            new PlaceMentionInput(PlaceText: "Village X", PlaceType: "village", NormalizedName: "village x"));

        await sourceRecords.AddKeywordAsync(created.SourceRecordId, "Семашко", "surname");
        // Re-adding the same keyword is a harmless no-op (insert-or-ignore).
        await sourceRecords.AddKeywordAsync(created.SourceRecordId, "Семашко", "surname");

        var detailAfterMentions = await sourceRecords.GetRecordAsync(created.SourceRecordId);
        Assert.NotNull(detailAfterMentions);
        var storedMention = Assert.Single(detailAfterMentions!.PersonMentions);
        Assert.Equal(mentionResult.PersonMentionId, storedMention.PersonMentionId);
        Assert.Equal("Семашко", storedMention.Surname);
        Assert.Equal("unlinked", storedMention.Status);
        Assert.Single(detailAfterMentions.PlaceMentions);
        Assert.Single(detailAfterMentions.Keywords);

        // Correcting the mention (personMentionId supplied) updates in place,
        // does not insert a second row.
        var correctedInput = mentionInput with { AgeText = "infant" };
        var corrected = await mentions.AddPersonMentionAsync(
            created.SourceRecordId, correctedInput, personMentionId: mentionResult.PersonMentionId);
        Assert.Equal(mentionResult.PersonMentionId, corrected.PersonMentionId);
        var detailAfterCorrection = await sourceRecords.GetRecordAsync(created.SourceRecordId);
        Assert.Single(detailAfterCorrection!.PersonMentions);
        Assert.Equal("infant", detailAfterCorrection.PersonMentions[0].AgeText);

        // (3) SearchAsync finds the record by surname, place, year range, keyword, freeText, linked=false.
        var bySurname = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, Surname: "Семашко"));
        Assert.Contains(bySurname.Records, r => r.SourceRecordId == created.SourceRecordId);

        var byPlace = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, Place: "Village X"));
        Assert.Contains(byPlace.Records, r => r.SourceRecordId == created.SourceRecordId);

        var byYearRange = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, YearFrom: 1849, YearTo: 1851));
        Assert.Contains(byYearRange.Records, r => r.SourceRecordId == created.SourceRecordId);

        var outsideYearRange = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, YearFrom: 1900, YearTo: 1950));
        Assert.DoesNotContain(outsideYearRange.Records, r => r.SourceRecordId == created.SourceRecordId);

        var byKeyword = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, Keyword: "Семашко"));
        Assert.Contains(byKeyword.Records, r => r.SourceRecordId == created.SourceRecordId);

        var byFreeText = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, FreeText: "village X"));
        Assert.Contains(byFreeText.Records, r => r.SourceRecordId == created.SourceRecordId);

        var unlinkedOnly = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, Linked: false));
        Assert.Contains(unlinkedOnly.Records, r => r.SourceRecordId == created.SourceRecordId);
        Assert.True(unlinkedOnly.TotalCount >= 1);

        // (4) SuggestLinksAsync returns candidate(s) with score+explanation; genealogy.* untouched.
        var personCountBefore = await CountTableAsync("genealogy.person");
        var nameCountBefore = await CountTableAsync("genealogy.person_name");

        var suggestions = await links.SuggestLinksAsync(mentionResult.PersonMentionId);
        Assert.NotEmpty(suggestions);
        var winningCandidate = Assert.Single(suggestions, c => c.PersonId == _semashkoPersonId);
        Assert.True(winningCandidate.Score > 0m);
        Assert.False(string.IsNullOrWhiteSpace(winningCandidate.Explanation));
        // The unrelated person never scores against a "Семашко" mention.
        Assert.DoesNotContain(suggestions, c => c.PersonId == _bondarPersonId);

        var personCountAfter = await CountTableAsync("genealogy.person");
        var nameCountAfter = await CountTableAsync("genealogy.person_name");
        Assert.Equal(personCountBefore, personCountAfter);
        Assert.Equal(nameCountBefore, nameCountAfter);

        var detailAfterSuggest = await sourceRecords.GetRecordAsync(created.SourceRecordId);
        Assert.Equal("suggested", detailAfterSuggest!.PersonMentions[0].Status);
        Assert.NotEmpty(detailAfterSuggest.LinkCandidates);

        // (5) AcceptLinkAsync sets mention.accepted_person_id + status accepted + confidence;
        // other candidates for the same mention become superseded.
        var accepted = await links.AcceptLinkAsync(winningCandidate.PersonLinkCandidateId);
        Assert.Equal(mentionResult.PersonMentionId, accepted.PersonMentionId);
        Assert.Equal(_semashkoPersonId, accepted.PersonId);
        Assert.Equal("accepted", accepted.Status);

        var detailAfterAccept = await sourceRecords.GetRecordAsync(created.SourceRecordId);
        var mentionAfterAccept = detailAfterAccept!.PersonMentions.Single(m => m.PersonMentionId == mentionResult.PersonMentionId);
        Assert.Equal("accepted", mentionAfterAccept.Status);
        Assert.Equal(_semashkoPersonId, mentionAfterAccept.AcceptedPersonId);
        Assert.Equal(winningCandidate.Score, mentionAfterAccept.Confidence);

        var acceptedCandidateRow = detailAfterAccept.LinkCandidates.Single(c => c.PersonLinkCandidateId == winningCandidate.PersonLinkCandidateId);
        Assert.Equal("accepted", acceptedCandidateRow.Status);

        var otherCandidatesForMention = detailAfterAccept.LinkCandidates
            .Where(c => c.PersonMentionId == mentionResult.PersonMentionId
                     && c.PersonLinkCandidateId != winningCandidate.PersonLinkCandidateId)
            .ToList();
        Assert.All(otherCandidatesForMention, c => Assert.Equal("superseded", c.Status));

        var linkedOnly = await search.SearchAsync(new ResearchSearchQuery(TreeId: _treeId, Linked: true));
        Assert.Contains(linkedOnly.Records, r => r.SourceRecordId == created.SourceRecordId);

        // (6) RejectLinkAsync on a fresh candidate -> rejected; re-suggest does not recreate it.
        var secondMentionInput = new PersonMentionInput(
            NameText: "Петро Бондар",
            GivenName: "Петро",
            Surname: "Бондар",
            Patronymic: null,
            Sex: 'M',
            Role: "witness",
            AgeText: null,
            EstimatedBirthYear: null,
            SocialStatus: null,
            RelationshipText: null);
        var secondMention = await mentions.AddPersonMentionAsync(created.SourceRecordId, secondMentionInput);

        var secondSuggestions = await links.SuggestLinksAsync(secondMention.PersonMentionId);
        var bondarCandidate = Assert.Single(secondSuggestions, c => c.PersonId == _bondarPersonId);

        var rejected = await links.RejectLinkAsync(bondarCandidate.PersonLinkCandidateId);
        Assert.Equal(bondarCandidate.PersonLinkCandidateId, rejected.PersonLinkCandidateId);
        Assert.Equal("rejected", rejected.Status);

        var thirdSuggestions = await links.SuggestLinksAsync(secondMention.PersonMentionId);
        Assert.DoesNotContain(thirdSuggestions, c => c.PersonId == _bondarPersonId);

        var rejectedRowStillExists = await CountRowsAsync(
            "SELECT count(*) FROM research.person_link_candidate WHERE person_mention_id = @mention_id AND person_id = @person_id AND status = 'rejected';",
            ("mention_id", secondMention.PersonMentionId),
            ("person_id", _bondarPersonId));
        Assert.Equal(1, rejectedRowStillExists);

        var duplicateRowCount = await CountRowsAsync(
            "SELECT count(*) FROM research.person_link_candidate WHERE person_mention_id = @mention_id AND person_id = @person_id;",
            ("mention_id", secondMention.PersonMentionId),
            ("person_id", _bondarPersonId));
        Assert.Equal(1, duplicateRowCount);
    }

    private async Task<long> CountTableAsync(string qualifiedTableName)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {qualifiedTableName};";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountRowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
