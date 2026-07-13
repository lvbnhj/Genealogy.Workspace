namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// The full result of <see cref="GedcomDuplicateService.ListCandidatesAsync"/>.
/// Mirrors the two result sets of the SQL Server source procedure
/// <c>ged.GetGedcomImportDuplicateCandidates</c>
/// (Database/Procedures/ged/GetGedcomImportDuplicateCandidates.sql): the
/// per-scope summary and the ranked candidate rows.
/// </summary>
public sealed record GedcomDuplicateListResult(
    IReadOnlyList<GedcomDuplicateSummaryRow> Summary,
    IReadOnlyList<GedcomDuplicateCandidateRow> Candidates);

/// <summary>
/// One row of the per-<c>candidate_scope</c> summary. Mirrors source result
/// set 1 (source lines 12-23).
/// </summary>
public sealed record GedcomDuplicateSummaryRow(
    string CandidateScope,
    long CandidateCount,
    long HighConfidenceCount,
    long ProbableCount,
    decimal MaxScore);

/// <summary>
/// One ranked candidate row with display names resolved for whichever sides
/// are populated. Mirrors source result set 2 (source lines 25-57).
/// </summary>
public sealed record GedcomDuplicateCandidateRow(
    long DuplicateCandidateId,
    string CandidateScope,
    Guid ImportTreePersonId1,
    string? ImportPerson1Name,
    Guid? ImportTreePersonId2,
    string? ImportPerson2Name,
    Guid? ExistingTreePersonId,
    string? ExistingPersonName,
    decimal Score,
    decimal NameScore,
    decimal DateScore,
    decimal PlaceScore,
    decimal FamilyScore,
    decimal EventScore,
    decimal NegativeScore,
    string? EvidenceFor,
    string? EvidenceAgainst,
    string RecommendedAction,
    string Status);

/// <summary>
/// The full result of <see cref="GedcomDuplicateService.GetCandidateDetailAsync"/>.
/// The header fields mirror source detail result set 1
/// (Database/Procedures/ged/GetGedcomImportDuplicateCandidateDetail.sql, lines
/// 7-42). <see cref="Events"/> mirrors source detail result set 2 (source
/// lines 44-140). <see cref="Parents"/>, <see cref="Children"/> and
/// <see cref="Spouses"/> are an EXPANDED addition beyond the source proc (per
/// plan decision): family-context evidence for each present side, built from
/// the staged <c>gedcom_import_parent_of</c> / <c>gedcom_import_family</c>
/// tables and the production <c>parent_child</c> / <c>family</c> tables.
/// </summary>
public sealed record GedcomDuplicateDetailResult(
    long DuplicateCandidateId,
    Guid ImportBatchId,
    string CandidateScope,
    Guid ImportTreePersonId1,
    string? ImportPerson1Name,
    string? ImportPerson1ExternalId,
    Guid? ImportTreePersonId2,
    string? ImportPerson2Name,
    string? ImportPerson2ExternalId,
    Guid? ExistingTreePersonId,
    string? ExistingPersonName,
    string? ExistingPersonExternalId,
    decimal Score,
    decimal NameScore,
    decimal DateScore,
    decimal PlaceScore,
    decimal FamilyScore,
    decimal EventScore,
    decimal NegativeScore,
    string? EvidenceFor,
    string? EvidenceAgainst,
    string RecommendedAction,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<GedcomDuplicateDetailEvent> Events,
    IReadOnlyList<GedcomDuplicateRelative> Parents,
    IReadOnlyList<GedcomDuplicateRelative> Children,
    IReadOnlyList<GedcomDuplicateSpouse> Spouses);

/// <summary>
/// One BIRT/CHR/DEAT/MARR event for one side (<c>import1</c>, <c>import2</c>
/// or <c>existing</c>) of a duplicate candidate. Mirrors source detail result
/// set 2's per-row shape (source lines 44-140): <see cref="HasSourceCitation"/>
/// and <see cref="CitationSummary"/> (top-3 citations, source-title/page/ref,
/// concatenated) are computed exactly as the source's <c>EXISTS</c> /
/// <c>STUFF(... FOR XML PATH(''))</c> pair, using PostgreSQL
/// <c>EXISTS</c> / <c>string_agg</c> instead.
/// </summary>
public sealed record GedcomDuplicateDetailEvent(
    string Side,
    string EventType,
    string? DateRaw,
    short? YearFrom,
    short? YearTo,
    string? PlaceRaw,
    string? EventValue,
    bool HasSourceCitation,
    string? CitationSummary);

/// <summary>
/// One parent or child of a candidate side (EXPANDED, beyond the source proc).
/// <see cref="PersonId"/> is the staged <c>tree_person_id</c> for the
/// <c>import1</c>/<c>import2</c> sides or the production <c>person_id</c> for
/// the <c>existing</c> side.
/// </summary>
public sealed record GedcomDuplicateRelative(
    string Side,
    Guid PersonId,
    string? DisplayName);

/// <summary>
/// One spouse of a candidate side, derived from the family table for that
/// side (EXPANDED, beyond the source proc — there is no dedicated spouse edge
/// table in either the staging or production schema).
/// </summary>
public sealed record GedcomDuplicateSpouse(
    string Side,
    Guid PersonId,
    string? DisplayName,
    short? MarriageYear);

/// <summary>
/// The outcome of <see cref="GedcomDuplicateService.RejectAsync"/>: the
/// candidate id and its new (<c>rejected</c>) status.
/// </summary>
public sealed record GedcomDuplicateRejectResult(
    long DuplicateCandidateId,
    string Status);

/// <summary>
/// Thrown by <see cref="GedcomDuplicateService.RejectAsync"/> when no
/// <c>genealogy.gedcom_import_duplicate_candidate</c> row has the given id.
/// </summary>
public sealed class GedcomDuplicateCandidateNotFoundException : Exception
{
    public GedcomDuplicateCandidateNotFoundException(long duplicateCandidateId)
        : base($"GEDCOM duplicate candidate not found: {duplicateCandidateId}.")
    {
        DuplicateCandidateId = duplicateCandidateId;
    }

    public long DuplicateCandidateId { get; }
}
