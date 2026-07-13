using System.Security.Cryptography;
using System.Text.Json;
using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Research;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Traversal;
using Genealogy.Workspace.McpServer.Tools;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Phase 6 Task 5 (final) exit criterion: reproduces, end-to-end through the
/// real MCP tools, the plan's primary Evidence Inbox user scenario
/// (docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10 Phase 6 exit criteria):
///
/// <list type="number">
/// <item>Create or select the Rudenko tree.</item>
/// <item>Add an inbox record with title, freetext and archive citation.</item>
/// <item>Store a screenshot in PostgreSQL and retrieve byte-identical content.</item>
/// <item>Add a "Семашко" person mention and place/year context.</item>
/// <item>Find the record by surname, place, year and freetext.</item>
/// <item>Suggest a tree person without modifying the tree.</item>
/// <item>Accept or reject the candidate explicitly.</item>
/// <item>(covered separately, in scripts/smoke.sh) Backup and restore the
/// database without losing the record or screenshot.</item>
/// </list>
///
/// Steps 1-7 are exercised here against a fresh, migrated database, wiring the
/// tools exactly as <see cref="ResearchToolsTests"/> and
/// <see cref="TreeToolsTests"/> do (real repositories/services/options, no
/// mocks). Step 8 (backup/restore) is a bash-level concern covered by the
/// seed/verify additions in <c>scripts/smoke.sh</c> — it cannot run inside a
/// C# test because it requires shelling out to <c>pg_dump</c>/<c>pg_restore</c>
/// against the docker-composed server, not an ephemeral per-test database.
///
/// The one tree person this scenario needs to suggest/accept/reject against
/// ("Іван Семашко") is seeded directly via <see cref="TestSeeding"/> (raw SQL),
/// because — deliberately — no Evidence Inbox tool creates or edits
/// <c>genealogy.person</c> rows; the only tool-level path to a tree person is
/// GEDCOM import, which is out of scope for this scenario. This mirrors
/// <see cref="ResearchToolsTests"/>'s own seeding of "Іван Семашко".
/// </summary>
public sealed class EvidenceInboxExitScenarioTests : IClassFixture<WorkspaceEnvironmentFixture>, IAsyncLifetime
{
    private readonly WorkspaceEnvironmentFixture _fixture;
    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;

    private TreeTools _treeTools = null!;
    private ResearchTools _researchTools = null!;
    private ResearchAttachmentTools _attachmentTools = null!;

    private Guid _semashkoPersonId;

    public EvidenceInboxExitScenarioTests(WorkspaceEnvironmentFixture fixture)
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

        _treeTools = new TreeTools(
            new TreeRepository(connectionFactory),
            new PersonRepository(connectionFactory),
            new PersonSearchRepository(connectionFactory),
            new RichFamilyContextRepository(connectionFactory),
            new PersonEventsRepository(connectionFactory),
            new TreeTraversalRepository(connectionFactory),
            new TreeResolver(connectionFactory),
            new PersonResolver(connectionFactory));

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
    public async Task PrimaryScenario_CreateTreeRecordAttachmentMentionSearchSuggestAcceptReject_Steps1Through7()
    {
        // ── Step 1: create or select the Rudenko tree ───────────────────────
        var createTreeJson = await _treeTools.CreateTreeDatasetAsync(
            name: "Rudenko", description: "Rudenko family research tree", isDefault: true);
        using var createTreeDoc = JsonDocument.Parse(createTreeJson);
        var createTreeRoot = createTreeDoc.RootElement;
        Assert.False(createTreeRoot.TryGetProperty("error", out _));
        var treeId = Guid.Parse(createTreeRoot.GetProperty("tree").GetProperty("treeId").GetString()!);
        Assert.Equal("Rudenko", createTreeRoot.GetProperty("tree").GetProperty("name").GetString());

        // Seed the one tree person the scenario suggests/accepts/rejects
        // against. No Evidence Inbox (or any other) tool creates tree
        // persons directly — that only happens via GEDCOM import, which is
        // out of scope here — so this uses the same raw-seeding approach as
        // ResearchToolsTests.
        _semashkoPersonId = Guid.NewGuid();
        await using (var seedConnection = new NpgsqlConnection(_connectionString))
        {
            await seedConnection.OpenAsync();

            await TestSeeding.InsertPersonAsync(
                seedConnection, treeId, _semashkoPersonId,
                primaryDisplayName: "Іван Семашко", surnameNormalized: "семашко", sex: 'M');
            await TestSeeding.InsertPersonNameAsync(
                seedConnection, treeId, _semashkoPersonId, scriptCode: "cyrl", nameType: "birth",
                fullName: "Іван Семашко", fullNameNormalized: "іван семашко", isPrimary: true,
                given: "Іван", surname: "Семашко");
            await TestSeeding.InsertEventAsync(
                seedConnection, treeId, _semashkoPersonId, eventType: "BIRT", yearFrom: 1850);
        }

        // Confirm the tree really does resolve by name for every subsequent
        // tree-scoped tool call (list_tree_datasets sanity check).
        var listTreesJson = await _treeTools.ListTreeDatasetsAsync();
        using var listTreesDoc = JsonDocument.Parse(listTreesJson);
        Assert.Contains(
            listTreesDoc.RootElement.GetProperty("trees").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "Rudenko");

        // ── Step 2: add an inbox record with title, freetext, archive citation ──
        const string freeText =
            "Народився Іван, син Петра Семашка, села Вишнівка, охрещений отцем Ковальським 1850 року.";

        var addRecordJson = await _researchTools.AddResearchRecordAsync(
            title: "Metric book: Rudenko births, village Вишнівка, 1850",
            recordType: "birth",
            recordText: freeText,
            transcription: freeText,
            archiveName: "TsDIAK",
            fond: "127",
            opis: "1015",
            sprava: "42",
            page: "17",
            citationText: "TsDIAK f.127 op.1015 spr.42 ark.17",
            tree: "Rudenko");
        using var addRecordDoc = JsonDocument.Parse(addRecordJson);
        var addRecordRoot = addRecordDoc.RootElement;
        Assert.False(addRecordRoot.TryGetProperty("error", out _));
        Assert.Equal("inbox", addRecordRoot.GetProperty("status").GetString());
        Assert.True(addRecordRoot.TryGetProperty("createdAt", out _));
        var sourceRecordId = addRecordRoot.GetProperty("sourceRecordId").GetString()!;

        // ── Step 3: store a screenshot in PostgreSQL, retrieve byte-identical ──
        var png = MakePng();
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(png));
        var base64Png = Convert.ToBase64String(png);

        var addAttachmentJson = await _attachmentTools.AddResearchAttachmentAsync(
            sourceRecordId, base64Content: base64Png, fileName: "scan.png",
            caption: "Metric book page scan", attachmentType: "image");
        using var addAttachmentDoc = JsonDocument.Parse(addAttachmentJson);
        var addAttachmentRoot = addAttachmentDoc.RootElement;
        Assert.False(addAttachmentRoot.TryGetProperty("error", out _));
        Assert.Equal("image/png", addAttachmentRoot.GetProperty("mimeType").GetString());
        Assert.False(addAttachmentRoot.GetProperty("deduplicated").GetBoolean());
        var attachmentId = addAttachmentRoot.GetProperty("sourceRecordAttachmentId").GetInt64();

        var getAttachmentJson = await _attachmentTools.GetResearchAttachmentAsync(attachmentId);
        using var getAttachmentDoc = JsonDocument.Parse(getAttachmentJson);
        var getAttachmentRoot = getAttachmentDoc.RootElement;
        Assert.False(getAttachmentRoot.TryGetProperty("error", out _));
        var roundTrippedBytes = Convert.FromBase64String(getAttachmentRoot.GetProperty("base64Content").GetString()!);
        var roundTrippedSha256 = Convert.ToHexString(SHA256.HashData(roundTrippedBytes));

        // Byte-identical, not merely same length: compare full SHA-256 digests
        // (and, belt-and-braces, the raw byte sequences themselves).
        Assert.Equal(expectedSha256, roundTrippedSha256);
        Assert.True(png.SequenceEqual(roundTrippedBytes), "attachment bytes must round-trip byte-identical");
        Assert.Equal(expectedSha256, getAttachmentRoot.GetProperty("contentHash").GetString(), ignoreCase: true);

        // ── Step 4: add a "Семашко" person mention + place/year context ────
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
            sourceRecordId, placeText: "Вишнівка", placeType: "village", normalizedName: "вишнівка");
        using var addPlaceDoc = JsonDocument.Parse(addPlaceJson);
        Assert.False(addPlaceDoc.RootElement.TryGetProperty("error", out _));

        // Year context, added on the record itself (step 4's "place/year
        // context" — the place came from add_record_place_mention above, the
        // year from update_research_record here).
        var setYearJson = await _researchTools.UpdateResearchRecordAsync(
            sourceRecordId, recordYearFrom: 1850, recordYearTo: 1850);
        using var setYearDoc = JsonDocument.Parse(setYearJson);
        Assert.False(setYearDoc.RootElement.TryGetProperty("error", out _));

        // ── Step 5: find the record by surname, place, year and freetext ───
        var searchJson = await _researchTools.SearchResearchRecordsAsync(
            surname: "Семашко",
            place: "Вишнівка",
            yearFrom: 1850,
            yearTo: 1850,
            query: "Ковальським",
            tree: "Rudenko");
        using var searchDoc = JsonDocument.Parse(searchJson);
        var searchRoot = searchDoc.RootElement;
        Assert.False(searchRoot.TryGetProperty("error", out _));
        var searchResults = searchRoot.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(searchResults, r => r.GetProperty("sourceRecordId").GetString() == sourceRecordId);
        Assert.True(searchRoot.GetProperty("totalCount").GetInt32() >= 1);

        // Cyrillic round-trips intact through the search result too.
        var foundRecord = searchResults.Single(r => r.GetProperty("sourceRecordId").GetString() == sourceRecordId);
        Assert.Contains("Вишнівка", foundRecord.GetProperty("title").GetString());

        // ── Step 6: suggest a tree person WITHOUT modifying the tree ───────
        await using var countConnection = new NpgsqlConnection(_connectionString);
        await countConnection.OpenAsync();

        var personCountBefore = await ScalarCountAsync(countConnection, "genealogy.person");
        var personNameCountBefore = await ScalarCountAsync(countConnection, "genealogy.person_name");

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

        var personCountAfter = await ScalarCountAsync(countConnection, "genealogy.person");
        var personNameCountAfter = await ScalarCountAsync(countConnection, "genealogy.person_name");

        Assert.Equal(personCountBefore, personCountAfter);
        Assert.Equal(personNameCountBefore, personNameCountAfter);

        // ── Step 7a: accept the candidate explicitly ────────────────────────
        var acceptJson = await _researchTools.AcceptRecordPersonLinkAsync(personLinkCandidateId);
        using var acceptDoc = JsonDocument.Parse(acceptJson);
        var acceptRoot = acceptDoc.RootElement;
        Assert.False(acceptRoot.TryGetProperty("error", out _));
        Assert.Equal("accepted", acceptRoot.GetProperty("status").GetString());
        Assert.Equal(_semashkoPersonId.ToString(), acceptRoot.GetProperty("personId").GetString());

        var getRecordJson = await _researchTools.GetResearchRecordAsync(sourceRecordId);
        using var getRecordDoc = JsonDocument.Parse(getRecordJson);
        var getRecordRoot = getRecordDoc.RootElement;
        Assert.False(getRecordRoot.TryGetProperty("error", out _));

        var mentionAfterAccept = Assert.Single(
            getRecordRoot.GetProperty("personMentions").EnumerateArray(),
            m => m.GetProperty("personMentionId").GetString() == personMentionId);
        Assert.Equal("accepted", mentionAfterAccept.GetProperty("status").GetString());
        Assert.Equal(_semashkoPersonId.ToString(), mentionAfterAccept.GetProperty("acceptedPersonId").GetString());
        // Cyrillic surname round-trips intact end-to-end.
        Assert.Equal("Семашко", mentionAfterAccept.GetProperty("surname").GetString());

        // The tree is still untouched after accept, too (accept only writes
        // to the research schema — genealogy.person/person_name are never
        // written by any Evidence Inbox tool).
        Assert.Equal(personCountBefore, await ScalarCountAsync(countConnection, "genealogy.person"));
        Assert.Equal(personNameCountBefore, await ScalarCountAsync(countConnection, "genealogy.person_name"));

        // ── Step 7b: reject path on a fresh candidate ───────────────────────
        // A second, independent record with its own "Семашко" mention, so the
        // reject path is exercised end-to-end (not simply the same candidate
        // already accepted above): suggest -> reject -> re-suggest must never
        // resurrect the rejected (mention, person) pair.
        var secondRecordJson = await _researchTools.AddResearchRecordAsync(
            title: "Metric book: another Rudenko entry", recordType: "birth", tree: "Rudenko");
        using var secondRecordDoc = JsonDocument.Parse(secondRecordJson);
        var secondSourceRecordId = secondRecordDoc.RootElement.GetProperty("sourceRecordId").GetString()!;

        var secondMentionJson = await _researchTools.AddRecordPersonMentionAsync(
            secondSourceRecordId, surname: "Семашко", givenName: "Іван", estimatedBirthYear: 1850);
        using var secondMentionDoc = JsonDocument.Parse(secondMentionJson);
        var secondPersonMentionId = secondMentionDoc.RootElement.GetProperty("personMentionId").GetString()!;

        var secondSuggestJson = await _researchTools.SuggestRecordPersonLinksAsync(secondPersonMentionId);
        using var secondSuggestDoc = JsonDocument.Parse(secondSuggestJson);
        var secondCandidate = secondSuggestDoc.RootElement.GetProperty("candidates").EnumerateArray()
            .Single(c => c.GetProperty("personId").GetString() == _semashkoPersonId.ToString());
        var secondCandidateId = secondCandidate.GetProperty("personLinkCandidateId").GetString()!;

        var rejectJson = await _researchTools.RejectRecordPersonLinkAsync(secondCandidateId);
        using var rejectDoc = JsonDocument.Parse(rejectJson);
        var rejectRoot = rejectDoc.RootElement;
        Assert.False(rejectRoot.TryGetProperty("error", out _));
        Assert.Equal("rejected", rejectRoot.GetProperty("status").GetString());

        var reSuggestJson = await _researchTools.SuggestRecordPersonLinksAsync(secondPersonMentionId);
        using var reSuggestDoc = JsonDocument.Parse(reSuggestJson);
        Assert.DoesNotContain(
            reSuggestDoc.RootElement.GetProperty("candidates").EnumerateArray(),
            c => c.GetProperty("personId").GetString() == _semashkoPersonId.ToString());

        // The reject path never touched the tree either.
        Assert.Equal(personCountBefore, await ScalarCountAsync(countConnection, "genealogy.person"));
        Assert.Equal(personNameCountBefore, await ScalarCountAsync(countConnection, "genealogy.person_name"));
    }

    // -- helpers --

    private static async Task<long> ScalarCountAsync(NpgsqlConnection connection, string qualifiedTableName)
    {
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {qualifiedTableName};", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Minimal but valid-signature PNG: the 8-byte signature followed by filler payload.</summary>
    private static byte[] MakePng(int payloadBytes = 32)
    {
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var result = new byte[signature.Length + payloadBytes];
        Array.Copy(signature, result, signature.Length);
        for (var i = signature.Length; i < result.Length; i++)
        {
            result[i] = (byte)(0x10 + (i % 0x0F));
        }

        return result;
    }
}
