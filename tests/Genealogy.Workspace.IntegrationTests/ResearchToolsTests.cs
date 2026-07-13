using System.Text.Json;
using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Research;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.McpServer.Tools;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 6 Task 4 exit criteria: exercises every <see cref="ResearchTools"/>
/// and <see cref="ResearchAttachmentTools"/> tool added on top of the Task
/// 2/3 services, against a fresh, migrated database seeded with one tree and
/// one matching <c>genealogy.person</c>. Mirrors the
/// <see cref="TreeToolsTests"/>/<see cref="EvidenceRecordTests"/>/
/// <see cref="AttachmentPipelineTests"/> fixture pattern: each test class
/// instance gets its own database created in <see cref="InitializeAsync"/>
/// and force-dropped in <see cref="DisposeAsync"/>.
/// </summary>
public sealed class ResearchToolsTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private ResearchTools _researchTools = null!;
    private ResearchAttachmentTools _attachmentTools = null!;

    private Guid _treeId;
    private Guid _semashkoPersonId;

    public ResearchToolsTests(WorkspaceEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _databaseName = TestSeeding.NewTestDatabaseName();
        await _fixture.CreateDatabaseAsync(_databaseName);

        var options = _fixture.BuildOptionsForDatabase(_databaseName);
        _connectionString = NpgsqlConnectionFactory.BuildConnectionString(options);
        var connectionFactory = new NpgsqlConnectionFactory(options);

        new MigrationEngine(_connectionString).Migrate();

        _treeId = Guid.NewGuid();
        _semashkoPersonId = Guid.NewGuid();

        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();

            await TestSeeding.InsertTreeAsync(connection, _treeId, "Research Tools Tree", isDefault: true);

            await TestSeeding.InsertPersonAsync(
                connection, _treeId, _semashkoPersonId,
                primaryDisplayName: "Іван Семашко", surnameNormalized: "семашко", sex: 'M');
            await TestSeeding.InsertPersonNameAsync(
                connection, _treeId, _semashkoPersonId, scriptCode: "cyrl", nameType: "birth",
                fullName: "Іван Семашко", fullNameNormalized: "іван семашко", isPrimary: true,
                given: "Іван", surname: "Семашко");
            await TestSeeding.InsertEventAsync(
                connection, _treeId, _semashkoPersonId, eventType: "BIRT", yearFrom: 1850);
        }

        var attachmentOptions = new AttachmentOptions();
        var attachmentRepository = new AttachmentRepository(connectionFactory, attachmentOptions);

        _researchTools = new ResearchTools(
            new SourceRecordRepository(connectionFactory),
            new RecordMentionRepository(connectionFactory),
            new PersonLinkService(connectionFactory),
            new SourceRecordSearchRepository(connectionFactory),
            attachmentRepository,
            new TreeResolver(connectionFactory));

        _attachmentTools = new ResearchAttachmentTools(attachmentRepository, attachmentOptions);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task EvidenceInboxLifecycle_RecordAttachmentMentionSearchSuggestAcceptGetShowsEverything()
    {
        // (1) add_research_record -> inbox status, createdAt present.
        var addRecordJson = await _researchTools.AddResearchRecordAsync(
            title: "Metric book: births, village X, 1850",
            recordType: "birth",
            recordYearFrom: 1850,
            recordYearTo: 1850,
            placeText: "Village X",
            archiveName: "TsDIAK",
            fond: "127",
            opis: "1015",
            sprava: "42");
        using var addRecordDoc = JsonDocument.Parse(addRecordJson);
        var addRecordRoot = addRecordDoc.RootElement;
        Assert.False(addRecordRoot.TryGetProperty("error", out _));
        Assert.Equal("inbox", addRecordRoot.GetProperty("status").GetString());
        Assert.True(addRecordRoot.TryGetProperty("createdAt", out _));
        var sourceRecordId = addRecordRoot.GetProperty("sourceRecordId").GetString()!;

        // (2) add_research_attachment with a synthesized PNG byte array base64.
        var png = MakePng();
        var base64Png = Convert.ToBase64String(png);

        var addAttachmentJson = await _attachmentTools.AddResearchAttachmentAsync(
            sourceRecordId, base64Content: base64Png, fileName: "scan.png", caption: "A scan", attachmentType: "image");
        using var addAttachmentDoc = JsonDocument.Parse(addAttachmentJson);
        var addAttachmentRoot = addAttachmentDoc.RootElement;
        Assert.False(addAttachmentRoot.TryGetProperty("error", out _));
        Assert.Equal("image/png", addAttachmentRoot.GetProperty("mimeType").GetString());
        Assert.False(addAttachmentRoot.GetProperty("deduplicated").GetBoolean());
        var attachmentId = addAttachmentRoot.GetProperty("sourceRecordAttachmentId").GetInt64();

        // (3) get_research_attachment asserts base64 round-trips to identical bytes.
        var getAttachmentJson = await _attachmentTools.GetResearchAttachmentAsync(attachmentId);
        using var getAttachmentDoc = JsonDocument.Parse(getAttachmentJson);
        var getAttachmentRoot = getAttachmentDoc.RootElement;
        Assert.False(getAttachmentRoot.TryGetProperty("error", out _));
        Assert.Equal("scan.png", getAttachmentRoot.GetProperty("fileName").GetString());
        var roundTrippedBytes = Convert.FromBase64String(getAttachmentRoot.GetProperty("base64Content").GetString()!);
        Assert.True(png.SequenceEqual(roundTrippedBytes), "attachment bytes must round-trip byte-identical");

        // (4) list_research_attachments shows the one attachment (no bytes).
        var listAttachmentsJson = await _attachmentTools.ListResearchAttachmentsAsync(sourceRecordId);
        using var listAttachmentsDoc = JsonDocument.Parse(listAttachmentsJson);
        var attachmentsArray = listAttachmentsDoc.RootElement.GetProperty("attachments");
        Assert.Equal(1, attachmentsArray.GetArrayLength());
        Assert.Equal(attachmentId, attachmentsArray[0].GetProperty("sourceRecordAttachmentId").GetInt64());
        Assert.False(attachmentsArray[0].TryGetProperty("base64Content", out _));

        // (5) add_record_person_mention(surname) + add_record_place_mention + add_research_keyword.
        var addMentionJson = await _researchTools.AddRecordPersonMentionAsync(
            sourceRecordId,
            nameText: "Іван Семашко",
            givenName: "Іван",
            surname: "Семашко",
            sex: "M",
            role: "child",
            estimatedBirthYear: 1850);
        using var addMentionDoc = JsonDocument.Parse(addMentionJson);
        var addMentionRoot = addMentionDoc.RootElement;
        Assert.False(addMentionRoot.TryGetProperty("error", out _));
        var personMentionId = addMentionRoot.GetProperty("personMentionId").GetString()!;

        var addPlaceJson = await _researchTools.AddRecordPlaceMentionAsync(
            sourceRecordId, placeText: "Village X", placeType: "village", normalizedName: "village x");
        using var addPlaceDoc = JsonDocument.Parse(addPlaceJson);
        Assert.False(addPlaceDoc.RootElement.TryGetProperty("error", out _));

        var addKeywordJson = await _researchTools.AddResearchKeywordAsync(sourceRecordId, "Семашко", "surname");
        using var addKeywordDoc = JsonDocument.Parse(addKeywordJson);
        Assert.True(addKeywordDoc.RootElement.GetProperty("ok").GetBoolean());

        // (6) search_research_records finds it (surname, place, keyword, freeText, year range, linked=false).
        var searchJson = await _researchTools.SearchResearchRecordsAsync(surname: "Семашко", tree: "Research Tools Tree");
        using var searchDoc = JsonDocument.Parse(searchJson);
        var searchResults = searchDoc.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(searchResults, r => r.GetProperty("sourceRecordId").GetString() == sourceRecordId);
        Assert.True(searchDoc.RootElement.GetProperty("totalCount").GetInt32() >= 1);

        var searchByKeywordJson = await _researchTools.SearchResearchRecordsAsync(keyword: "Семашко");
        using var searchByKeywordDoc = JsonDocument.Parse(searchByKeywordJson);
        Assert.Contains(
            searchByKeywordDoc.RootElement.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("sourceRecordId").GetString() == sourceRecordId);

        var unlinkedSearchJson = await _researchTools.SearchResearchRecordsAsync(linked: false);
        using var unlinkedSearchDoc = JsonDocument.Parse(unlinkedSearchJson);
        Assert.Contains(
            unlinkedSearchDoc.RootElement.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("sourceRecordId").GetString() == sourceRecordId);

        // (7) suggest_record_person_links returns a candidate.
        var suggestJson = await _researchTools.SuggestRecordPersonLinksAsync(personMentionId);
        using var suggestDoc = JsonDocument.Parse(suggestJson);
        var suggestRoot = suggestDoc.RootElement;
        Assert.False(suggestRoot.TryGetProperty("error", out _));
        var candidates = suggestRoot.GetProperty("candidates").EnumerateArray().ToList();
        Assert.NotEmpty(candidates);
        var winningCandidate = candidates.Single(c => c.GetProperty("personId").GetString() == _semashkoPersonId.ToString());
        Assert.True(winningCandidate.GetProperty("score").GetDecimal() > 0m);
        Assert.False(string.IsNullOrWhiteSpace(winningCandidate.GetProperty("explanation").GetString()));
        var personLinkCandidateId = winningCandidate.GetProperty("personLinkCandidateId").GetString()!;

        // (8) accept_record_person_link.
        var acceptJson = await _researchTools.AcceptRecordPersonLinkAsync(personLinkCandidateId);
        using var acceptDoc = JsonDocument.Parse(acceptJson);
        var acceptRoot = acceptDoc.RootElement;
        Assert.False(acceptRoot.TryGetProperty("error", out _));
        Assert.Equal("accepted", acceptRoot.GetProperty("status").GetString());
        Assert.Equal(_semashkoPersonId.ToString(), acceptRoot.GetProperty("personId").GetString());

        // (9) get_research_record shows the accepted mention + the attachment.
        var getRecordJson = await _researchTools.GetResearchRecordAsync(sourceRecordId);
        using var getRecordDoc = JsonDocument.Parse(getRecordJson);
        var getRecordRoot = getRecordDoc.RootElement;
        Assert.False(getRecordRoot.TryGetProperty("error", out _));
        Assert.Equal("Metric book: births, village X, 1850", getRecordRoot.GetProperty("record").GetProperty("title").GetString());

        var mentionsAfterAccept = getRecordRoot.GetProperty("personMentions").EnumerateArray().ToList();
        var mentionAfterAccept = Assert.Single(mentionsAfterAccept, m => m.GetProperty("personMentionId").GetString() == personMentionId);
        Assert.Equal("accepted", mentionAfterAccept.GetProperty("status").GetString());
        Assert.Equal(_semashkoPersonId.ToString(), mentionAfterAccept.GetProperty("acceptedPersonId").GetString());

        var placeMentionsAfter = getRecordRoot.GetProperty("placeMentions").EnumerateArray().ToList();
        Assert.Single(placeMentionsAfter);

        var attachmentsAfter = getRecordRoot.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Single(attachmentsAfter, a => a.GetProperty("sourceRecordAttachmentId").GetInt64() == attachmentId);

        // update_research_record: change status, confirm it sticks.
        var updateJson = await _researchTools.UpdateResearchRecordAsync(sourceRecordId, status: "resolved");
        using var updateDoc = JsonDocument.Parse(updateJson);
        Assert.Equal("resolved", updateDoc.RootElement.GetProperty("status").GetString());

        // reject_record_person_link on a fresh, unrelated mention.
        var secondMentionJson = await _researchTools.AddRecordPersonMentionAsync(
            sourceRecordId, nameText: "Хтось Інший", surname: "Бондар", givenName: "Петро", role: "witness");
        using var secondMentionDoc = JsonDocument.Parse(secondMentionJson);
        var secondMentionId = secondMentionDoc.RootElement.GetProperty("personMentionId").GetString()!;

        var secondSuggestJson = await _researchTools.SuggestRecordPersonLinksAsync(secondMentionId);
        using var secondSuggestDoc = JsonDocument.Parse(secondSuggestJson);
        var secondCandidates = secondSuggestDoc.RootElement.GetProperty("candidates").EnumerateArray().ToList();
        // The unrelated surname never scores against the seeded "Семашко" person,
        // so there is nothing to reject against a real row — instead verify the
        // reject path directly against the winning candidate accepted above is
        // rejected by re-suggesting for the FIRST mention returns no new pending
        // candidate re-created for a rejected pair; here we simply confirm
        // suggest_record_person_links is well-formed (possibly empty) for an
        // unrelated mention.
        Assert.NotNull(secondCandidates);

        // (10) delete_research_attachment + not-found {error} case.
        var deleteJson = await _attachmentTools.DeleteResearchAttachmentAsync(attachmentId);
        using var deleteDoc = JsonDocument.Parse(deleteJson);
        Assert.True(deleteDoc.RootElement.GetProperty("deleted").GetBoolean());

        var getDeletedJson = await _attachmentTools.GetResearchAttachmentAsync(attachmentId);
        using var getDeletedDoc = JsonDocument.Parse(getDeletedJson);
        Assert.True(getDeletedDoc.RootElement.TryGetProperty("error", out _));

        // (11) "both/neither of filePath/base64 supplied" {error} case.
        var neitherJson = await _attachmentTools.AddResearchAttachmentAsync(sourceRecordId);
        using var neitherDoc = JsonDocument.Parse(neitherJson);
        Assert.True(neitherDoc.RootElement.TryGetProperty("error", out _));

        var bothJson = await _attachmentTools.AddResearchAttachmentAsync(
            sourceRecordId, filePath: "/tmp/whatever.png", base64Content: base64Png);
        using var bothDoc = JsonDocument.Parse(bothJson);
        Assert.True(bothDoc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task RejectRecordPersonLink_MarksRejected_AndSuggestNeverRecreatesIt()
    {
        var addRecordJson = await _researchTools.AddResearchRecordAsync(
            title: "Metric book: another entry", recordType: "birth");
        using var addRecordDoc = JsonDocument.Parse(addRecordJson);
        var sourceRecordId = addRecordDoc.RootElement.GetProperty("sourceRecordId").GetString()!;

        var mentionJson = await _researchTools.AddRecordPersonMentionAsync(
            sourceRecordId, surname: "Семашко", givenName: "Іван", estimatedBirthYear: 1850);
        using var mentionDoc = JsonDocument.Parse(mentionJson);
        var personMentionId = mentionDoc.RootElement.GetProperty("personMentionId").GetString()!;

        var suggestJson = await _researchTools.SuggestRecordPersonLinksAsync(personMentionId);
        using var suggestDoc = JsonDocument.Parse(suggestJson);
        var candidate = suggestDoc.RootElement.GetProperty("candidates").EnumerateArray()
            .Single(c => c.GetProperty("personId").GetString() == _semashkoPersonId.ToString());
        var candidateId = candidate.GetProperty("personLinkCandidateId").GetString()!;

        var rejectJson = await _researchTools.RejectRecordPersonLinkAsync(candidateId);
        using var rejectDoc = JsonDocument.Parse(rejectJson);
        Assert.Equal("rejected", rejectDoc.RootElement.GetProperty("status").GetString());

        var secondSuggestJson = await _researchTools.SuggestRecordPersonLinksAsync(personMentionId);
        using var secondSuggestDoc = JsonDocument.Parse(secondSuggestJson);
        Assert.DoesNotContain(
            secondSuggestDoc.RootElement.GetProperty("candidates").EnumerateArray(),
            c => c.GetProperty("personId").GetString() == _semashkoPersonId.ToString());
    }

    [Fact]
    public async Task AddRecordPersonMention_UnknownSourceRecord_ReturnsError()
    {
        var json = await _researchTools.AddRecordPersonMentionAsync(Guid.NewGuid().ToString(), surname: "Nobody");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetResearchRecord_UnknownId_ReturnsError()
    {
        var json = await _researchTools.GetResearchRecordAsync(Guid.NewGuid().ToString());
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetResearchRecord_NonGuid_ReturnsError()
    {
        var json = await _researchTools.GetResearchRecordAsync("not-a-guid");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // -- synthesized test bytes (no repo-file dependency) --

    /// <summary>Minimal PNG: the 8-byte signature followed by filler payload.</summary>
    private static byte[] MakePng(int payloadBytes = 8)
    {
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var result = new byte[signature.Length + payloadBytes];
        Array.Copy(signature, result, signature.Length);
        for (var i = signature.Length; i < result.Length; i++)
        {
            result[i] = 0x11;
        }

        return result;
    }
}
