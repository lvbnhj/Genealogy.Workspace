using System.ComponentModel;
using System.Text.Json;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Staging;
using ModelContextProtocol.Server;

namespace Genealogy.Workspace.McpServer.Tools;

/// <summary>
/// Thin wrappers over the Phase 3/4 GEDCOM import services: stage a GEDCOM
/// file into the <c>genealogy.gedcom_import_*</c> staging tables, preview and
/// review it (readiness report, duplicate candidates), and either cancel it or
/// apply it to the production tree. Mirrors the SQL Server
/// <c>DnaAnalysis.McpServer.Tools.GedcomImportTools</c> tool names, parameter
/// names, and JSON field names — product-neutral, no DNA terminology. Every
/// tool resolves its own <c>tree</c>/<c>importBatchId</c> input and maps the
/// underlying service's typed exceptions to a plain <c>{ error }</c> payload,
/// mirroring <see cref="TreeTools"/>'s try/catch convention. Nothing here
/// enforces the readiness report's advisory gates: <c>apply_gedcom_import</c>
/// does not consult them (the plan's explicit "no gate" decision), and
/// <c>dryRun</c> defaults to <c>true</c> so a real apply always requires an
/// explicit opt-in.
/// </summary>
[McpServerToolType]
public sealed class GedcomTools(
    GedcomStagingService stagingService,
    GedcomImportPreviewService previewService,
    GedcomDuplicateService duplicateService,
    GedcomReadinessService readinessService,
    GedcomApplyService applyService,
    TreeResolver treeResolver)
{
    [McpServerTool(Name = "stage_gedcom_import")]
    [Description("Stages a GEDCOM file for import by running the deterministic GEDCOM preflight exporter and loading the generated rows into genealogy.gedcom_import_* staging tables. Does not apply changes to the production tree.")]
    public async Task<string> StageGedcomImportAsync(
        [Description("Absolute or repository-relative path to the .ged file on the MCP host")] string filePath,
        [Description("Target tree name or GUID. Defaults to the workspace's default tree. Create new trees with create_tree_dataset first.")] string? tree = null,
        [Description("Root person GEDCOM xref or unambiguous name substring, e.g. @I1@ or 'Jane Doe'")] string? root = null,
        [Description("Optional notes stored on the import batch header")] string? notes = null,
        [Description("Use legacy xref-only id derivation instead of tree-scoped keys")] bool legacyIds = false,
        [Description("Optional fixed import batch GUID. Usually omit this.")] string? batchId = null,
        [Description("Optional output directory for generated staging artifacts. Defaults to a fresh temp directory.")] string? outputDirectory = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            Guid? parsedBatchId = null;
            if (!string.IsNullOrWhiteSpace(batchId))
            {
                if (!Guid.TryParse(batchId, out var parsed))
                {
                    return JsonSerializer.Serialize(new { error = "batchId must be a GUID." }, McpJson.Options);
                }

                parsedBatchId = parsed;
            }

            var request = new GedcomStageRequest
            {
                GedcomFilePath = filePath,
                TreeId = treeRes.TreeId,
                TreeName = treeRes.Name,
                RootXref = root,
                Notes = notes,
                LegacyIds = legacyIds,
                BatchId = parsedBatchId,
                OutputDirectory = outputDirectory,
            };

            var result = await stagingService.StageAsync(request);

            return JsonSerializer.Serialize(new
            {
                importBatchId = result.BatchId,
                tree = MapTree(treeRes),
                rowCounts = result.RowCounts,
                note = "GEDCOM was staged. No production tree changes were applied.",
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[stage_gedcom_import] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_gedcom_import_preview")]
    [Description("Returns a read-only preview summary for a staged GEDCOM import batch: counts of rows that would be added or updated, plus a capped sample of person-level changes. Does not apply changes.")]
    public async Task<string> GetGedcomImportPreviewAsync(
        [Description("GEDCOM import batch GUID")] string importBatchId)
    {
        try
        {
            if (!Guid.TryParse(importBatchId, out var batchId))
            {
                return JsonSerializer.Serialize(new { error = "importBatchId must be a GUID." }, McpJson.Options);
            }

            var preview = await previewService.PreviewAsync(batchId);

            return JsonSerializer.Serialize(new
            {
                batch = MapPreviewBatch(preview.Batch),
                changes = preview.Changes.Select(MapChange),
                samplePersonChanges = preview.SamplePersonChanges.Select(MapPersonChange),
                note = "Read-only preview. No GEDCOM changes were applied.",
            }, McpJson.Options);
        }
        catch (GedcomImportBatchNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_gedcom_import_preview] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "list_pending_gedcom_imports")]
    [Description("Lists staged/previewed/waiting GEDCOM import batches that have not been applied or cancelled.")]
    public async Task<string> ListPendingGedcomImportsAsync(
        [Description("Optional tree name or GUID filter. Defaults to all trees.")] string? tree = null,
        [Description("Include PREVIEWED batches as pending. Defaults to true.")] bool includePreviewed = true,
        [Description("Maximum rows to return. Defaults to 50.")] int topN = 50)
    {
        try
        {
            Guid? treeId = null;
            if (!string.IsNullOrWhiteSpace(tree))
            {
                var treeRes = await treeResolver.ResolveTreeAsync(tree);
                if (!treeRes.IsResolved) return TreeErrorJson(treeRes);
                treeId = treeRes.TreeId;
            }

            var rows = await stagingService.ListPendingAsync(treeId, includePreviewed, topN);

            return JsonSerializer.Serialize(new
            {
                tree,
                includePreviewed,
                pendingImports = rows.Select(MapPendingImport),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[list_pending_gedcom_imports] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "cancel_gedcom_import")]
    [Description("Cancels a staged GEDCOM import batch so it will not be applied. Does not delete staging rows and cannot cancel an already applied batch.")]
    public async Task<string> CancelGedcomImportAsync(
        [Description("GEDCOM import batch GUID")] string importBatchId,
        [Description("Optional human-readable cancellation reason stored in batch notes")] string? reason = null)
    {
        try
        {
            if (!Guid.TryParse(importBatchId, out var batchId))
            {
                return JsonSerializer.Serialize(new { error = "importBatchId must be a GUID." }, McpJson.Options);
            }

            var result = await stagingService.CancelAsync(batchId, reason);

            return JsonSerializer.Serialize(new
            {
                result = MapCancelResult(result),
                note = "GEDCOM import batch was cancelled. No production tree changes were applied.",
            }, McpJson.Options);
        }
        catch (GedcomImportBatchNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (GedcomImportBatchAlreadyAppliedException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[cancel_gedcom_import] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_gedcom_import_readiness_report")]
    [Description("Runs GEDCOM import readiness checks for a staged batch: duplicate-candidate regeneration and four advisory gates (high-confidence duplicates, name parsing issues, date warnings, scope-invalid rows). Advisory only — does not enforce or block apply_gedcom_import.")]
    public async Task<string> GetGedcomImportReadinessReportAsync(
        [Description("GEDCOM import batch GUID")] string importBatchId,
        [Description("Minimum duplicate score to regenerate and report. Defaults to 0.75.")] decimal minDuplicateScore = 0.75m)
    {
        try
        {
            if (!Guid.TryParse(importBatchId, out var batchId))
            {
                return JsonSerializer.Serialize(new { error = "importBatchId must be a GUID." }, McpJson.Options);
            }

            var report = await readinessService.GetReadinessAsync(batchId, minDuplicateScore);

            return JsonSerializer.Serialize(new
            {
                importBatchId = report.ImportBatchId,
                status = report.Status,
                gates = report.Gates.Select(MapGate),
                canApplyWithoutReview = report.CanApplyWithoutReview,
                requiresExplicitConfirmation = report.RequiresExplicitConfirmation,
                duplicateCount = report.DuplicateCount,
            }, McpJson.Options);
        }
        catch (GedcomImportBatchNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_gedcom_import_readiness_report] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "find_import_duplicate_candidates")]
    [Description("Generates and returns read-only duplicate candidates for a staged GEDCOM import batch. Does not merge, apply, or change production tree rows.")]
    public async Task<string> FindImportDuplicateCandidatesAsync(
        [Description("GEDCOM import batch GUID")] string importBatchId,
        [Description("Minimum score to generate and return. Defaults to 0.75.")] decimal minScore = 0.75m,
        [Description("Maximum candidate rows to return. Defaults to 100.")] int topN = 100)
    {
        try
        {
            if (!Guid.TryParse(importBatchId, out var batchId))
            {
                return JsonSerializer.Serialize(new { error = "importBatchId must be a GUID." }, McpJson.Options);
            }

            await duplicateService.GenerateAsync(batchId, minScore);
            var result = await duplicateService.ListCandidatesAsync(batchId, minScore, topN);

            return JsonSerializer.Serialize(new
            {
                importBatchId = batchId,
                minScore,
                result = new
                {
                    summary = result.Summary.Select(MapDuplicateSummary),
                    candidates = result.Candidates.Select(MapDuplicateCandidate),
                },
                note = "Read-only duplicate preflight. No merge/apply changes were performed.",
            }, McpJson.Options);
        }
        catch (GedcomImportBatchNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[find_import_duplicate_candidates] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_duplicate_candidate_detail")]
    [Description("Returns detailed evidence for one GEDCOM import duplicate candidate: header, matching BIRT/CHR/DEAT/MARR events, and parents/children/spouses for each present side.")]
    public async Task<string> GetDuplicateCandidateDetailAsync(
        [Description("Duplicate candidate id")] long duplicateCandidateId)
    {
        try
        {
            var detail = await duplicateService.GetCandidateDetailAsync(duplicateCandidateId);
            if (detail is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"GEDCOM duplicate candidate not found: {duplicateCandidateId}." }, McpJson.Options);
            }

            return JsonSerializer.Serialize(new
            {
                candidate = MapDuplicateDetail(detail),
                events = detail.Events.Select(MapDetailEvent),
                parents = detail.Parents.Select(MapRelative),
                spouses = detail.Spouses.Select(MapSpouse),
                children = detail.Children.Select(MapRelative),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_duplicate_candidate_detail] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "reject_tree_person_merge_candidate")]
    [Description("Rejects a GEDCOM import duplicate candidate so regeneration does not re-suggest it for the same batch. Does not merge or alter tree persons.")]
    public async Task<string> RejectTreePersonMergeCandidateAsync(
        [Description("Duplicate candidate id")] long duplicateCandidateId)
    {
        try
        {
            var result = await duplicateService.RejectAsync(duplicateCandidateId);

            return JsonSerializer.Serialize(new
            {
                result = new { duplicateCandidateId = result.DuplicateCandidateId, status = result.Status },
                note = "Candidate rejected only. No merge/apply changes were performed.",
            }, McpJson.Options);
        }
        catch (GedcomDuplicateCandidateNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[reject_tree_person_merge_candidate] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "apply_gedcom_import")]
    [Description("Applies a staged GEDCOM import batch. dryRun defaults to true and only returns the change summary the real apply would produce, with zero side effects. Set dryRun=false only after reviewing the preview/readiness report. Set deleteMissing=true explicitly to delete tree persons missing from the new GEDCOM.")]
    public async Task<string> ApplyGedcomImportAsync(
        [Description("GEDCOM import batch GUID")] string importBatchId,
        [Description("When dryRun=false, delete tree persons missing from the GEDCOM plus their dependent rows. Defaults to false and must be set explicitly. Ignored in dry-run mode.")] bool deleteMissing = false,
        [Description("Preview only when true. Defaults to true. Set false only after reviewing the preview.")] bool dryRun = true)
    {
        try
        {
            if (!Guid.TryParse(importBatchId, out var batchId))
            {
                return JsonSerializer.Serialize(new { error = "importBatchId must be a GUID." }, McpJson.Options);
            }

            var result = await applyService.ApplyAsync(batchId, deleteMissing, dryRun);

            return JsonSerializer.Serialize(new
            {
                applyStatus = new { importBatchId = batchId, dryRun = result.DryRun, status = result.Status },
                changes = result.Changes.Select(MapApplyChange),
                note = result.Note,
            }, McpJson.Options);
        }
        catch (GedcomImportBatchNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[apply_gedcom_import] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object MapTree(TreeResolution resolution) => new
    {
        treeId = resolution.TreeId,
        name = resolution.Name,
        isDefault = resolution.IsDefault,
    };

    private static string TreeErrorJson(TreeResolution resolution) =>
        JsonSerializer.Serialize(new { error = resolution.FailureReason }, McpJson.Options);

    private static object MapPreviewBatch(GedcomImportPreviewBatch b) => new
    {
        importBatchId = b.ImportBatchId,
        treeId = b.TreeId,
        treeName = b.TreeName,
        sourceFilePath = b.SourceFilePath,
        sourceFileHash = b.SourceFileHash,
        rootExternalId = b.RootExternalId,
        rootPersonId = b.RootPersonId,
        personCount = b.PersonCount,
        familyCount = b.FamilyCount,
        eventCount = b.EventCount,
        placeCount = b.PlaceCount,
        scopeInvalidCount = b.ScopeInvalidCount,
        status = b.Status,
        createdAt = b.CreatedAt,
        previewedAt = b.PreviewedAt,
        appliedAt = b.AppliedAt,
        notes = b.Notes,
    };

    private static object MapChange(GedcomImportPreviewChange c) => new
    {
        entityType = c.EntityType,
        changeType = c.ChangeType,
        rowCount = c.RowCount,
    };

    private static object MapPersonChange(GedcomImportPreviewPersonChange c) => new
    {
        entityType = c.EntityType,
        changeType = c.ChangeType,
        treePersonId = c.TreePersonId,
        externalId = c.ExternalId,
        primaryDisplayName = c.PrimaryDisplayName,
        currentPrimaryDisplayName = c.CurrentPrimaryDisplayName,
        sex = c.Sex,
        currentSex = c.CurrentSex,
    };

    private static object MapPendingImport(GedcomPendingImport p) => new
    {
        importBatchId = p.ImportBatchId,
        treeId = p.TreeId,
        treeName = p.TreeName,
        rootExternalId = p.RootExternalId,
        rootPersonId = p.RootPersonId,
        rootName = p.RootName,
        sourceFilePath = p.SourceFilePath,
        sourceFileHash = p.SourceFileHash,
        personCount = p.PersonCount,
        familyCount = p.FamilyCount,
        eventCount = p.EventCount,
        placeCount = p.PlaceCount,
        scopeInvalidCount = p.ScopeInvalidCount,
        status = p.Status,
        createdAt = p.CreatedAt,
        previewedAt = p.PreviewedAt,
        appliedAt = p.AppliedAt,
        cancelledAt = p.CancelledAt,
        notes = p.Notes,
        nameIssueCount = p.NameIssueCount,
        dateWarningCount = p.DateWarningCount,
    };

    private static object MapCancelResult(GedcomCancelResult r) => new
    {
        importBatchId = r.ImportBatchId,
        treeId = r.TreeId,
        status = r.Status,
        createdAt = r.CreatedAt,
        previewedAt = r.PreviewedAt,
        appliedAt = r.AppliedAt,
        cancelledAt = r.CancelledAt,
        notes = r.Notes,
    };

    private static object MapGate(GedcomReadinessGate g) => new
    {
        gate = g.Gate,
        severity = g.Severity,
        count = g.Count,
    };

    private static object MapDuplicateSummary(GedcomDuplicateSummaryRow s) => new
    {
        candidateScope = s.CandidateScope,
        candidateCount = s.CandidateCount,
        highConfidenceCount = s.HighConfidenceCount,
        probableCount = s.ProbableCount,
        maxScore = s.MaxScore,
    };

    private static object MapDuplicateCandidate(GedcomDuplicateCandidateRow c) => new
    {
        duplicateCandidateId = c.DuplicateCandidateId,
        candidateScope = c.CandidateScope,
        importTreePersonId1 = c.ImportTreePersonId1,
        importPerson1Name = c.ImportPerson1Name,
        importTreePersonId2 = c.ImportTreePersonId2,
        importPerson2Name = c.ImportPerson2Name,
        existingTreePersonId = c.ExistingTreePersonId,
        existingPersonName = c.ExistingPersonName,
        score = c.Score,
        nameScore = c.NameScore,
        dateScore = c.DateScore,
        placeScore = c.PlaceScore,
        familyScore = c.FamilyScore,
        eventScore = c.EventScore,
        negativeScore = c.NegativeScore,
        evidenceFor = c.EvidenceFor,
        evidenceAgainst = c.EvidenceAgainst,
        recommendedAction = c.RecommendedAction,
        status = c.Status,
    };

    private static object MapDuplicateDetail(GedcomDuplicateDetailResult d) => new
    {
        duplicateCandidateId = d.DuplicateCandidateId,
        importBatchId = d.ImportBatchId,
        candidateScope = d.CandidateScope,
        importTreePersonId1 = d.ImportTreePersonId1,
        importPerson1Name = d.ImportPerson1Name,
        importPerson1ExternalId = d.ImportPerson1ExternalId,
        importTreePersonId2 = d.ImportTreePersonId2,
        importPerson2Name = d.ImportPerson2Name,
        importPerson2ExternalId = d.ImportPerson2ExternalId,
        existingTreePersonId = d.ExistingTreePersonId,
        existingPersonName = d.ExistingPersonName,
        existingPersonExternalId = d.ExistingPersonExternalId,
        score = d.Score,
        nameScore = d.NameScore,
        dateScore = d.DateScore,
        placeScore = d.PlaceScore,
        familyScore = d.FamilyScore,
        eventScore = d.EventScore,
        negativeScore = d.NegativeScore,
        evidenceFor = d.EvidenceFor,
        evidenceAgainst = d.EvidenceAgainst,
        recommendedAction = d.RecommendedAction,
        status = d.Status,
        createdAt = d.CreatedAt,
        updatedAt = d.UpdatedAt,
    };

    private static object MapDetailEvent(GedcomDuplicateDetailEvent e) => new
    {
        side = e.Side,
        eventType = e.EventType,
        dateRaw = e.DateRaw,
        yearFrom = e.YearFrom,
        yearTo = e.YearTo,
        placeRaw = e.PlaceRaw,
        eventValue = e.EventValue,
        hasSourceCitation = e.HasSourceCitation,
        citationSummary = e.CitationSummary,
    };

    private static object MapRelative(GedcomDuplicateRelative r) => new
    {
        side = r.Side,
        personId = r.PersonId,
        displayName = r.DisplayName,
    };

    private static object MapSpouse(GedcomDuplicateSpouse s) => new
    {
        side = s.Side,
        personId = s.PersonId,
        displayName = s.DisplayName,
        marriageYear = s.MarriageYear,
    };

    private static object MapApplyChange(GedcomApplyChange c) => new
    {
        entityType = c.EntityType,
        changeType = c.ChangeType,
        rowCount = c.RowCount,
    };
}
