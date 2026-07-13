using System.ComponentModel;
using System.Text.Json;
using Genealogy.Workspace.Data.Research;
using ModelContextProtocol.Server;

namespace Genealogy.Workspace.McpServer.Tools;

/// <summary>
/// Evidence Inbox binary attachment tools: add a screenshot/document to a
/// research source record (from a local file path or a base64 payload),
/// fetch its bytes back out, list a record's attachments (metadata only), and
/// delete an attachment link. Thin wrapper over <see cref="AttachmentRepository"/>
/// (Task 2) — the server always derives the MIME type/hash/length from the
/// bytes it receives and never trusts a client-declared type or extension.
///
/// Unlike <see cref="ResearchTools"/>'s GUID ids, the ids here
/// (<c>sourceRecordAttachmentId</c>) are the schema's sequential bigint
/// primary keys, so they are ordinary <c>long</c> tool parameters — no GUID
/// parsing needed. <c>sourceRecordId</c> stays a GUID string, consistent with
/// every other Evidence Inbox tool.
/// </summary>
[McpServerToolType]
public sealed class ResearchAttachmentTools(
    AttachmentRepository attachmentRepository,
    AttachmentOptions attachmentOptions)
{
    [McpServerTool(Name = "add_research_attachment")]
    [Description("Adds a binary attachment (screenshot/document) to a research source record, from EITHER a local file path OR a base64 payload (exactly one of filePath/base64Content must be supplied). The server derives the MIME type from the bytes' magic numbers (PNG, JPEG, GIF, WEBP, TIFF, PDF only) and computes its SHA-256 hash; identical bytes already stored elsewhere are deduplicated (deduplicated=true) rather than re-inserted.")]
    public async Task<string> AddResearchAttachmentAsync(
        [Description("Source record GUID")] string sourceRecordId,
        [Description("Absolute or repository-relative path to the file on the MCP host. Supply this OR base64Content, not both.")] string? filePath = null,
        [Description("Base64-encoded file content. Supply this OR filePath, not both. Subject to a tighter decoded-size ceiling than a local file.")] string? base64Content = null,
        [Description("File name to record. Defaults to the source file's name when filePath is used.")] string? fileName = null,
        [Description("Optional caption/description")] string? caption = null,
        [Description("Attachment category: 'image', 'document', or omit for 'other'")] string? attachmentType = null,
        [Description("Display order among the record's attachments (default 0)")] int sequenceNo = 0,
        [Description("Optional URL the attachment was sourced from")] string? sourceUrl = null)
    {
        try
        {
            if (!Guid.TryParse(sourceRecordId, out var recordId))
            {
                return JsonSerializer.Serialize(new { error = "sourceRecordId must be a GUID." }, McpJson.Options);
            }

            var hasFilePath = !string.IsNullOrWhiteSpace(filePath);
            var hasBase64 = !string.IsNullOrWhiteSpace(base64Content);
            if (hasFilePath == hasBase64)
            {
                return JsonSerializer.Serialize(
                    new { error = "Supply exactly one of filePath or base64Content." }, McpJson.Options);
            }

            byte[] content;
            string? resolvedFileName = fileName;

            if (hasBase64)
            {
                content = AttachmentRepository.DecodeBase64(base64Content!, attachmentOptions.MaxBase64Bytes);
            }
            else
            {
                if (!File.Exists(filePath))
                {
                    return JsonSerializer.Serialize(
                        new { error = $"File not found: {filePath}" }, McpJson.Options);
                }

                content = await File.ReadAllBytesAsync(filePath!);
                resolvedFileName ??= Path.GetFileName(filePath!);
            }

            var added = await attachmentRepository.AddAttachmentAsync(
                recordId, content, resolvedFileName, caption, attachmentType, sequenceNo, sourceUrl);

            return JsonSerializer.Serialize(new
            {
                sourceRecordAttachmentId = added.SourceRecordAttachmentId,
                attachmentContentId = added.AttachmentContentId,
                contentHash = added.ContentHash,
                byteLength = added.ByteLength,
                mimeType = added.MimeType,
                deduplicated = added.Deduplicated,
            }, McpJson.Options);
        }
        catch (AttachmentTooLargeException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (AttachmentTypeNotAllowedException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (RecordAttachmentQuotaExceededException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[add_research_attachment] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_research_attachment")]
    [Description("Fetches one attachment's bytes (base64-encoded) plus its metadata, byte-identical to what was stored.")]
    public async Task<string> GetResearchAttachmentAsync(
        [Description("Source record attachment id")] long sourceRecordAttachmentId)
    {
        try
        {
            var attachment = await attachmentRepository.GetAttachmentAsync(sourceRecordAttachmentId);
            if (attachment is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"Attachment {sourceRecordAttachmentId} was not found." }, McpJson.Options);
            }

            return JsonSerializer.Serialize(new
            {
                sourceRecordAttachmentId = attachment.SourceRecordAttachmentId,
                fileName = attachment.FileName,
                caption = attachment.Caption,
                mimeType = attachment.MimeType,
                byteLength = attachment.ByteLength,
                contentHash = attachment.ContentHash,
                base64Content = Convert.ToBase64String(attachment.Content),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_research_attachment] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "list_research_attachments")]
    [Description("Lists a source record's attachments (metadata only, no bytes — use get_research_attachment to fetch bytes), ordered by sequence number.")]
    public async Task<string> ListResearchAttachmentsAsync(
        [Description("Source record GUID")] string sourceRecordId)
    {
        try
        {
            if (!Guid.TryParse(sourceRecordId, out var recordId))
            {
                return JsonSerializer.Serialize(new { error = "sourceRecordId must be a GUID." }, McpJson.Options);
            }

            var attachments = await attachmentRepository.ListAttachmentsAsync(recordId);

            return JsonSerializer.Serialize(new
            {
                attachments = attachments.Select(a => new
                {
                    sourceRecordAttachmentId = a.SourceRecordAttachmentId,
                    fileName = a.FileName,
                    caption = a.Caption,
                    attachmentType = a.AttachmentType,
                    sequenceNo = a.SequenceNo,
                    mimeType = a.MimeType,
                    byteLength = a.ByteLength,
                    contentHash = a.ContentHash,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[list_research_attachments] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "delete_research_attachment")]
    [Description("Deletes ONLY an attachment's link to its source record. The underlying content is retained (it may be shared with other records) and is reclaimed only by a separate, explicit cleanup step — never as a side effect of this call.")]
    public async Task<string> DeleteResearchAttachmentAsync(
        [Description("Source record attachment id")] long sourceRecordAttachmentId)
    {
        try
        {
            var deleted = await attachmentRepository.DeleteAttachmentLinkAsync(sourceRecordAttachmentId);

            return JsonSerializer.Serialize(new
            {
                sourceRecordAttachmentId,
                deleted,
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[delete_research_attachment] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }
}
