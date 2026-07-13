using System.ComponentModel;
using System.Text.Json;
using Genealogy.Workspace.Data.Research;
using Genealogy.Workspace.Data.Resolvers;
using ModelContextProtocol.Server;
using Npgsql;

namespace Genealogy.Workspace.McpServer.Tools;

/// <summary>
/// Evidence Inbox tools: create/read/update research source records (church
/// register entries, archive files, etc.), add the people and places they
/// mention, search across them, and score/accept/reject candidate links from
/// a mention to an existing tree person. Layered directly over the Task 3
/// services (<see cref="SourceRecordRepository"/>, <see cref="RecordMentionRepository"/>,
/// <see cref="PersonLinkService"/>, <see cref="SourceRecordSearchRepository"/>)
/// plus <see cref="AttachmentRepository"/> (only for composing
/// <c>get_research_record</c>'s attachment list — see <see cref="ResearchAttachmentTools"/>
/// for the binary attachment tools themselves). Every id parameter here is a
/// GUID string parsed with <see cref="Guid.TryParse"/> (an unparsable value
/// returns <c>{ error }</c>, mirroring <see cref="GedcomTools"/>'s
/// <c>importBatchId</c> handling) — <c>research.*</c> primary keys are
/// app-generated uuids, unlike the sequential bigint attachment ids in
/// <see cref="ResearchAttachmentTools"/>. <c>suggest_record_person_links</c>
/// is guaranteed read-only against the tree by the underlying service. No DNA
/// terminology anywhere — this is the product-neutral genealogy workspace
/// server.
/// </summary>
[McpServerToolType]
public sealed class ResearchTools(
    SourceRecordRepository sourceRecordRepository,
    RecordMentionRepository mentionRepository,
    PersonLinkService linkService,
    SourceRecordSearchRepository searchRepository,
    AttachmentRepository attachmentRepository,
    TreeResolver treeResolver)
{
    [McpServerTool(Name = "add_research_record")]
    [Description("Adds a new Evidence Inbox source record (a church register entry, archive file, etc.) to a genealogy tree. Starts in the 'inbox' status unless status is given explicitly.")]
    public async Task<string> AddResearchRecordAsync(
        [Description("Short title for the record")] string title,
        [Description("Record type, e.g. 'birth', 'death', 'marriage', 'census'")] string recordType,
        [Description("Full transcribed or summarized text of the record")] string? recordText = null,
        [Description("Verbatim transcription, if distinct from recordText")] string? transcription = null,
        [Description("Free-text record date as written in the source")] string? recordDateText = null,
        [Description("Earliest year the record could date to")] int? recordYearFrom = null,
        [Description("Latest year the record could date to")] int? recordYearTo = null,
        [Description("Place named on the record")] string? placeText = null,
        [Description("Church/parish named on the record")] string? churchText = null,
        [Description("Archive holding the record")] string? archiveName = null,
        [Description("Archive fond (collection) reference")] string? fond = null,
        [Description("Archive opis (inventory) reference")] string? opis = null,
        [Description("Archive sprava (file) reference")] string? sprava = null,
        [Description("Page/folio reference within the file")] string? page = null,
        [Description("Formatted citation text")] string? citationText = null,
        [Description("URL to an online copy of the record")] string? sourceUrl = null,
        [Description("Workflow status: 'inbox', 'in_review', 'resolved', 'dismissed', or 'archived'. Defaults to 'inbox' when omitted.")] string? status = null,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var input = new SourceRecordInput(
                TreeId: treeRes.TreeId,
                Title: title,
                RecordType: recordType,
                RecordText: recordText,
                Transcription: transcription,
                RecordDateText: recordDateText,
                RecordYearFrom: (short?)recordYearFrom,
                RecordYearTo: (short?)recordYearTo,
                PlaceText: placeText,
                ChurchText: churchText,
                ArchiveName: archiveName,
                Fond: fond,
                Opis: opis,
                Sprava: sprava,
                Page: page,
                CitationText: citationText,
                SourceUrl: sourceUrl,
                Status: status);

            var created = await sourceRecordRepository.AddRecordAsync(input);

            return JsonSerializer.Serialize(new
            {
                sourceRecordId = created.SourceRecordId,
                status = created.Status,
                createdAt = created.CreatedAt,
            }, McpJson.Options);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[add_research_record] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_research_record")]
    [Description("Returns the full Evidence Inbox card for one research source record: its own fields, keywords, person mentions, place mentions, person-link candidates, and its attachment list (metadata only, no bytes — use get_research_attachment for bytes).")]
    public async Task<string> GetResearchRecordAsync(
        [Description("Source record GUID")] string sourceRecordId)
    {
        try
        {
            if (!TryParseGuid(sourceRecordId, nameof(sourceRecordId), out var recordId, out var parseError)) return parseError!;

            var detail = await sourceRecordRepository.GetRecordAsync(recordId);
            if (detail is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"Source record '{sourceRecordId}' was not found." }, McpJson.Options);
            }

            var attachments = await attachmentRepository.ListAttachmentsAsync(recordId);

            return JsonSerializer.Serialize(new
            {
                record = MapRecordCore(detail),
                keywords = detail.Keywords.Select(k => new { keyword = k.Keyword, keywordType = k.KeywordType }),
                personMentions = detail.PersonMentions.Select(MapPersonMention),
                placeMentions = detail.PlaceMentions.Select(MapPlaceMention),
                linkCandidates = detail.LinkCandidates.Select(MapLinkCandidate),
                attachments = attachments.Select(MapAttachmentInfo),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_research_record] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "update_research_record")]
    [Description("Updates the given fields of a research source record, including its workflow status. A field left null is unchanged.")]
    public async Task<string> UpdateResearchRecordAsync(
        [Description("Source record GUID")] string sourceRecordId,
        [Description("Short title for the record")] string? title = null,
        [Description("Record type, e.g. 'birth', 'death', 'marriage', 'census'")] string? recordType = null,
        [Description("Full transcribed or summarized text of the record")] string? recordText = null,
        [Description("Verbatim transcription, if distinct from recordText")] string? transcription = null,
        [Description("Free-text record date as written in the source")] string? recordDateText = null,
        [Description("Earliest year the record could date to")] int? recordYearFrom = null,
        [Description("Latest year the record could date to")] int? recordYearTo = null,
        [Description("Place named on the record")] string? placeText = null,
        [Description("Church/parish named on the record")] string? churchText = null,
        [Description("Archive holding the record")] string? archiveName = null,
        [Description("Archive fond (collection) reference")] string? fond = null,
        [Description("Archive opis (inventory) reference")] string? opis = null,
        [Description("Archive sprava (file) reference")] string? sprava = null,
        [Description("Page/folio reference within the file")] string? page = null,
        [Description("Formatted citation text")] string? citationText = null,
        [Description("URL to an online copy of the record")] string? sourceUrl = null,
        [Description("Workflow status: 'inbox', 'in_review', 'resolved', 'dismissed', or 'archived'")] string? status = null)
    {
        try
        {
            if (!TryParseGuid(sourceRecordId, nameof(sourceRecordId), out var recordId, out var parseError)) return parseError!;

            var updated = await sourceRecordRepository.UpdateRecordAsync(
                recordId,
                title: title,
                recordType: recordType,
                recordText: recordText,
                transcription: transcription,
                recordDateText: recordDateText,
                recordYearFrom: (short?)recordYearFrom,
                recordYearTo: (short?)recordYearTo,
                placeText: placeText,
                churchText: churchText,
                archiveName: archiveName,
                fond: fond,
                opis: opis,
                sprava: sprava,
                page: page,
                citationText: citationText,
                sourceUrl: sourceUrl,
                status: status);

            if (updated is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"Source record '{sourceRecordId}' was not found." }, McpJson.Options);
            }

            return JsonSerializer.Serialize(new
            {
                sourceRecordId = updated.SourceRecordId,
                status = updated.Status,
                updatedAt = updated.UpdatedAt,
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[update_research_record] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "search_research_records")]
    [Description("Searches Evidence Inbox source records in a tree by any combination of status, record type, free text, person/place fields, archive citation fields, year range, keyword, mention role, and link status. Every filter besides tree is optional; an absent filter never restricts the result set.")]
    public async Task<string> SearchResearchRecordsAsync(
        [Description("Workflow status filter")] string? status = null,
        [Description("Record type filter, e.g. 'birth', 'death', 'marriage'")] string? recordType = null,
        [Description("Free-text substring match against title/recordText/transcription")] string? query = null,
        [Description("Surname substring match against the record's person mentions")] string? surname = null,
        [Description("Given name substring match against the record's person mentions")] string? givenName = null,
        [Description("Place substring match against the record's place fields/mentions")] string? place = null,
        [Description("Archive name prefix match")] string? archiveName = null,
        [Description("Fond (collection) prefix match")] string? fond = null,
        [Description("Opis (inventory) prefix match")] string? opis = null,
        [Description("Sprava (file) prefix match")] string? sprava = null,
        [Description("Page/folio prefix match")] string? page = null,
        [Description("Only records whose year span overlaps at or after this year")] int? yearFrom = null,
        [Description("Only records whose year span overlaps at or before this year")] int? yearTo = null,
        [Description("Keyword substring match against the record's attached keywords")] string? keyword = null,
        [Description("Only records with a person mention in this role, e.g. 'witness', 'child'")] string? role = null,
        [Description("true = only records with at least one accepted person mention; false = only records with none; omit for no filter")] bool? linked = null,
        [Description("Maximum rows to return (default 50)")] int topN = 50,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var searchQuery = new ResearchSearchQuery(
                TreeId: treeRes.TreeId,
                Status: status,
                RecordType: recordType,
                FreeText: query,
                Surname: surname,
                GivenName: givenName,
                Place: place,
                ArchiveName: archiveName,
                Fond: fond,
                Opis: opis,
                Sprava: sprava,
                Page: page,
                YearFrom: (short?)yearFrom,
                YearTo: (short?)yearTo,
                Keyword: keyword,
                Role: role,
                Linked: linked,
                TopN: topN);

            var results = await searchRepository.SearchAsync(searchQuery);

            return JsonSerializer.Serialize(new
            {
                results = results.Records.Select(MapSearchItem),
                totalCount = results.TotalCount,
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[search_research_records] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "add_record_person_mention")]
    [Description("Adds a person mentioned in a research source record (e.g. a witness, parent, or child named in the record text), or — when personMentionId is supplied — corrects an existing mention's descriptive fields on that same record instead of inserting a new one. A new mention always starts unlinked; use suggest_record_person_links / accept_record_person_link to connect it to a tree person.")]
    public async Task<string> AddRecordPersonMentionAsync(
        [Description("Source record GUID")] string sourceRecordId,
        [Description("Full name as written in the record")] string? nameText = null,
        [Description("Given name")] string? givenName = null,
        [Description("Surname")] string? surname = null,
        [Description("Patronymic")] string? patronymic = null,
        [Description("Sex: M or F")] string? sex = null,
        [Description("Role in the record, e.g. 'child', 'father', 'mother', 'witness'")] string? role = null,
        [Description("Age as written in the record, e.g. '25 years'")] string? ageText = null,
        [Description("Estimated birth year, derived from age/context")] int? estimatedBirthYear = null,
        [Description("Social status/estate as written in the record")] string? socialStatus = null,
        [Description("Free-text description of this person's relationship to others in the record")] string? relationshipText = null,
        [Description("Existing person mention GUID to correct instead of creating a new mention")] string? personMentionId = null)
    {
        try
        {
            if (!TryParseGuid(sourceRecordId, nameof(sourceRecordId), out var recordId, out var recordError)) return recordError!;

            Guid? existingMentionId = null;
            if (!string.IsNullOrWhiteSpace(personMentionId))
            {
                if (!TryParseGuid(personMentionId, nameof(personMentionId), out var parsedMentionId, out var mentionError)) return mentionError!;
                existingMentionId = parsedMentionId;
            }

            char? sexChar = null;
            if (!string.IsNullOrWhiteSpace(sex))
            {
                sexChar = char.ToUpperInvariant(sex.Trim()[0]);
            }

            var input = new PersonMentionInput(
                NameText: nameText,
                GivenName: givenName,
                Surname: surname,
                Patronymic: patronymic,
                Sex: sexChar,
                Role: role,
                AgeText: ageText,
                EstimatedBirthYear: (short?)estimatedBirthYear,
                SocialStatus: socialStatus,
                RelationshipText: relationshipText);

            var result = await mentionRepository.AddPersonMentionAsync(recordId, input, existingMentionId);

            return JsonSerializer.Serialize(new
            {
                personMentionId = result.PersonMentionId,
                sourceRecordId = result.SourceRecordId,
            }, McpJson.Options);
        }
        catch (SourceRecordNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (PersonMentionNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[add_record_person_mention] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "add_record_place_mention")]
    [Description("Adds a place mentioned in a research source record.")]
    public async Task<string> AddRecordPlaceMentionAsync(
        [Description("Source record GUID")] string sourceRecordId,
        [Description("Place name as written in the record")] string placeText,
        [Description("Place type, e.g. 'village', 'parish', 'district'")] string? placeType = null,
        [Description("Normalized/standardized place name")] string? normalizedName = null,
        [Description("Optional link to a genealogy.place row id")] long? placeId = null)
    {
        try
        {
            if (!TryParseGuid(sourceRecordId, nameof(sourceRecordId), out var recordId, out var recordError)) return recordError!;

            var input = new PlaceMentionInput(placeText, placeType, normalizedName, placeId);
            var result = await mentionRepository.AddPlaceMentionAsync(recordId, input);

            return JsonSerializer.Serialize(new
            {
                placeMentionId = result.PlaceMentionId,
                sourceRecordId = result.SourceRecordId,
            }, McpJson.Options);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return JsonSerializer.Serialize(
                new { error = $"Source record '{sourceRecordId}' was not found." }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[add_record_place_mention] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "add_research_keyword")]
    [Description("Attaches a denormalized search keyword to a research source record. Re-adding an existing (keyword, keywordType) pair is a harmless no-op.")]
    public async Task<string> AddResearchKeywordAsync(
        [Description("Source record GUID")] string sourceRecordId,
        [Description("Keyword text")] string keyword,
        [Description("Keyword category, e.g. 'surname', 'place', 'occupation'")] string keywordType)
    {
        try
        {
            if (!TryParseGuid(sourceRecordId, nameof(sourceRecordId), out var recordId, out var recordError)) return recordError!;

            await sourceRecordRepository.AddKeywordAsync(recordId, keyword, keywordType);

            return JsonSerializer.Serialize(new { ok = true }, McpJson.Options);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return JsonSerializer.Serialize(
                new { error = $"Source record '{sourceRecordId}' was not found." }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[add_research_keyword] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "suggest_record_person_links")]
    [Description("Scores candidate tree persons in the mention's own tree against its surname/given name/estimated birth year, persists each surviving candidate, and returns them ranked by score. READ-ONLY against the tree: never inserts/updates/deletes a genealogy.person row; only ever writes the scored candidates to the research schema.")]
    public async Task<string> SuggestRecordPersonLinksAsync(
        [Description("Person mention GUID")] string personMentionId,
        [Description("Maximum candidates to return (default 10)")] int topN = 10)
    {
        try
        {
            if (!TryParseGuid(personMentionId, nameof(personMentionId), out var mentionId, out var parseError)) return parseError!;

            var candidates = await linkService.SuggestLinksAsync(mentionId, topN);

            return JsonSerializer.Serialize(new
            {
                personMentionId = mentionId,
                candidates = candidates.Select(c => new
                {
                    personLinkCandidateId = c.PersonLinkCandidateId,
                    personId = c.PersonId,
                    fullName = c.FullName,
                    score = c.Score,
                    explanation = c.Explanation,
                }),
            }, McpJson.Options);
        }
        catch (PersonMentionNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[suggest_record_person_links] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "accept_record_person_link")]
    [Description("Accepts a person-link candidate: marks it accepted, sets the mention's accepted person/status/confidence, and supersedes the mention's other still-suggested candidates.")]
    public async Task<string> AcceptRecordPersonLinkAsync(
        [Description("Person link candidate GUID")] string personLinkCandidateId)
    {
        try
        {
            if (!TryParseGuid(personLinkCandidateId, nameof(personLinkCandidateId), out var candidateId, out var parseError)) return parseError!;

            var result = await linkService.AcceptLinkAsync(candidateId);

            return JsonSerializer.Serialize(new
            {
                personMentionId = result.PersonMentionId,
                personId = result.PersonId,
                status = result.Status,
            }, McpJson.Options);
        }
        catch (PersonLinkCandidateNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[accept_record_person_link] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "reject_record_person_link")]
    [Description("Rejects a person-link candidate. Never touches the parent mention's accepted link. The rejected row is kept (not deleted) so suggest_record_person_links never recreates it for the same mention/person pair.")]
    public async Task<string> RejectRecordPersonLinkAsync(
        [Description("Person link candidate GUID")] string personLinkCandidateId)
    {
        try
        {
            if (!TryParseGuid(personLinkCandidateId, nameof(personLinkCandidateId), out var candidateId, out var parseError)) return parseError!;

            var result = await linkService.RejectLinkAsync(candidateId);

            return JsonSerializer.Serialize(new
            {
                personLinkCandidateId = result.PersonLinkCandidateId,
                status = result.Status,
            }, McpJson.Options);
        }
        catch (PersonLinkCandidateNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[reject_record_person_link] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseGuid(string value, string paramName, out Guid result, out string? errorJson)
    {
        if (!Guid.TryParse(value, out result))
        {
            errorJson = JsonSerializer.Serialize(new { error = $"{paramName} must be a GUID." }, McpJson.Options);
            return false;
        }

        errorJson = null;
        return true;
    }

    private static string TreeErrorJson(TreeResolution resolution) =>
        JsonSerializer.Serialize(new { error = resolution.FailureReason }, McpJson.Options);

    private static object MapRecordCore(SourceRecordDetail d) => new
    {
        sourceRecordId = d.SourceRecordId,
        treeId = d.TreeId,
        title = d.Title,
        recordType = d.RecordType,
        recordText = d.RecordText,
        transcription = d.Transcription,
        recordDateText = d.RecordDateText,
        recordDateFrom = d.RecordDateFrom,
        recordDateTo = d.RecordDateTo,
        recordYearFrom = d.RecordYearFrom,
        recordYearTo = d.RecordYearTo,
        placeText = d.PlaceText,
        churchText = d.ChurchText,
        archiveName = d.ArchiveName,
        fond = d.Fond,
        opis = d.Opis,
        sprava = d.Sprava,
        page = d.Page,
        citationText = d.CitationText,
        sourceUrl = d.SourceUrl,
        status = d.Status,
        createdAt = d.CreatedAt,
        updatedAt = d.UpdatedAt,
    };

    private static object MapPersonMention(PersonMentionEntry m) => new
    {
        personMentionId = m.PersonMentionId,
        nameText = m.NameText,
        givenName = m.GivenName,
        surname = m.Surname,
        patronymic = m.Patronymic,
        sex = m.Sex?.ToString(),
        role = m.Role,
        ageText = m.AgeText,
        estimatedBirthYear = m.EstimatedBirthYear,
        socialStatus = m.SocialStatus,
        relationshipText = m.RelationshipText,
        status = m.Status,
        acceptedPersonId = m.AcceptedPersonId,
        confidence = m.Confidence,
        createdAt = m.CreatedAt,
        updatedAt = m.UpdatedAt,
    };

    private static object MapPlaceMention(PlaceMentionEntry p) => new
    {
        placeMentionId = p.PlaceMentionId,
        placeText = p.PlaceText,
        placeType = p.PlaceType,
        normalizedName = p.NormalizedName,
        placeId = p.PlaceId,
    };

    private static object MapLinkCandidate(LinkCandidateEntry c) => new
    {
        personLinkCandidateId = c.PersonLinkCandidateId,
        personMentionId = c.PersonMentionId,
        personId = c.PersonId,
        score = c.Score,
        explanation = c.Explanation,
        status = c.Status,
        createdAt = c.CreatedAt,
        decidedAt = c.DecidedAt,
    };

    private static object MapAttachmentInfo(AttachmentInfo a) => new
    {
        sourceRecordAttachmentId = a.SourceRecordAttachmentId,
        fileName = a.FileName,
        caption = a.Caption,
        attachmentType = a.AttachmentType,
        sequenceNo = a.SequenceNo,
        sourceUrl = a.SourceUrl,
        mimeType = a.MimeType,
        byteLength = a.ByteLength,
        contentHash = a.ContentHash,
        createdAt = a.CreatedAt,
    };

    private static object MapSearchItem(SourceRecordSearchItem r) => new
    {
        sourceRecordId = r.SourceRecordId,
        title = r.Title,
        recordType = r.RecordType,
        status = r.Status,
        recordYearFrom = r.RecordYearFrom,
        recordYearTo = r.RecordYearTo,
        placeText = r.PlaceText,
        churchText = r.ChurchText,
        archiveName = r.ArchiveName,
        fond = r.Fond,
        opis = r.Opis,
        sprava = r.Sprava,
        page = r.Page,
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
    };
}
