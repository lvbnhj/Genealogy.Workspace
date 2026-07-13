namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// The full ADVISORY readiness report for a staged GEDCOM import batch.
/// Mirrors the JSON shape built by
/// <c>GedcomImportTools.GetGedcomImportReadinessReportAsync</c> /
/// <c>BuildReadinessGates</c>
/// (McpServer/DnaAnalysis.McpServer/Tools/GedcomImportTools.cs, ~lines 700-749):
/// four labelled gates plus report-level flags. Nothing in this workspace
/// enforces these labels -- there is no confirmation token and
/// <c>apply_gedcom_import</c> does not consult this report (the plan's
/// explicit "no gate" decision). <see cref="Status"/> reflects the batch
/// status AFTER the STAGED/PREVIEWED -> WAITING_FOR_CONFIRMATION transition
/// performed by <see cref="GedcomReadinessService.GetReadinessAsync"/>.
/// </summary>
public sealed record GedcomReadinessReport(
    Guid ImportBatchId,
    string Status,
    IReadOnlyList<GedcomReadinessGate> Gates,
    bool CanApplyWithoutReview,
    bool RequiresExplicitConfirmation,
    long DuplicateCount);

/// <summary>
/// One advisory gate row, as returned by the STABLE SQL function
/// <c>genealogy.gedcom_import_readiness_gates</c> (migration 0007):
/// <c>high_confidence_duplicates</c>, <c>name_parsing_issues</c>,
/// <c>date_warnings</c>, or <c>scope_invalid</c>, each with a severity of
/// <c>blocker</c>, <c>warning</c>, or <c>pass</c> and the underlying count.
/// These labels are advisory only; see <see cref="GedcomReadinessReport"/>.
/// </summary>
public sealed record GedcomReadinessGate(
    string Gate,
    string Severity,
    long Count);
