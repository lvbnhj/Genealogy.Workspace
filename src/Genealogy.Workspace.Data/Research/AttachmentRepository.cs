using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Stores and retrieves binary evidence (screenshots/documents) as PostgreSQL
/// <c>bytea</c> against the <c>research.attachment_content</c> /
/// <c>research.source_record_attachment</c> tables (migration 0009).
///
/// Trust model: the DATABASE bytes are the durable copy. The server always
/// computes the SHA-256 hash, byte length and MIME type from the bytes it
/// receives; it never trusts a client-declared type or file extension, and it
/// never executes attachment content. Identical bytes are stored exactly once
/// (dedup by content hash). Deleting a link removes only the link row — shared
/// content survives and is reclaimed only by an explicit orphan cleanup.
/// </summary>
public sealed class AttachmentRepository
{
    private readonly NpgsqlConnectionFactory _connectionFactory;
    private readonly AttachmentOptions _options;

    public AttachmentRepository(NpgsqlConnectionFactory connectionFactory, AttachmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _connectionFactory = connectionFactory;
        _options = options;
    }

    /// <summary>
    /// Adds an attachment to a source record. Pipeline (all limits enforced
    /// server-side, all DB work in one transaction):
    ///   (a) per-file size limit (<see cref="AttachmentOptions.MaxFileBytes"/>);
    ///   (b) MIME allowlist via <see cref="MimeSniffer"/> (magic bytes only);
    ///   (c) SHA-256 of the bytes;
    ///   (d) per-record aggregate quota (<see cref="AttachmentOptions.MaxRecordBytes"/>)
    ///       over the record's DISTINCT linked content plus this content (unless
    ///       it is already linked/deduped to this record);
    ///   (e) DEDUP: reuse an existing content row with the same hash, else insert;
    ///   (f) insert the link row.
    /// </summary>
    /// <exception cref="AttachmentTooLargeException">Bytes exceed <see cref="AttachmentOptions.MaxFileBytes"/>.</exception>
    /// <exception cref="AttachmentTypeNotAllowedException">Magic bytes are not in the allowlist.</exception>
    /// <exception cref="RecordAttachmentQuotaExceededException">The per-record byte quota would be exceeded.</exception>
    public async Task<AddAttachmentResult> AddAttachmentAsync(
        Guid sourceRecordId,
        byte[] content,
        string? fileName,
        string? caption,
        string? attachmentType,
        int sequenceNo,
        string? sourceUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // (a) per-file size limit.
        if (content.LongLength > _options.MaxFileBytes)
        {
            throw new AttachmentTooLargeException(content.LongLength, _options.MaxFileBytes, "per-file");
        }

        // (b) MIME allowlist — server derives the type from the bytes; the
        // client-declared attachmentType is only a categorization hint below.
        if (!MimeSniffer.TrySniff(content, out var mimeType))
        {
            throw new AttachmentTypeNotAllowedException();
        }

        // (c) SHA-256 hex (lowercase, 64 chars) — the dedup key.
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
        var byteLength = content.LongLength;
        var normalizedType = NormalizeAttachmentType(attachmentType);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // (e, first half) DEDUP lookup: is this content already stored?
            var existingContentId = await FindContentByHashAsync(
                connection, contentHash, cancellationToken).ConfigureAwait(false);

            // Is this exact content already linked to THIS record? If so it adds
            // no new DISTINCT bytes to the record's quota.
            var alreadyLinkedToRecord = existingContentId is not null &&
                await IsContentLinkedToRecordAsync(
                    connection, sourceRecordId, existingContentId.Value, cancellationToken)
                    .ConfigureAwait(false);

            // (d) per-record aggregate quota over DISTINCT linked content.
            var currentDistinctBytes = await SumDistinctRecordBytesAsync(
                connection, sourceRecordId, cancellationToken).ConfigureAwait(false);
            var additionalBytes = alreadyLinkedToRecord ? 0 : byteLength;
            if (currentDistinctBytes + additionalBytes > _options.MaxRecordBytes)
            {
                throw new RecordAttachmentQuotaExceededException(
                    sourceRecordId, currentDistinctBytes, additionalBytes, _options.MaxRecordBytes);
            }

            // (e, second half) reuse existing content or insert new bytes once.
            long contentId;
            bool deduplicated;
            if (existingContentId is not null)
            {
                contentId = existingContentId.Value;
                deduplicated = true;
            }
            else
            {
                contentId = await InsertContentAsync(
                    connection, contentHash, content, byteLength, mimeType, cancellationToken)
                    .ConfigureAwait(false);
                deduplicated = false;
            }

            // (f) insert the link row.
            var linkId = await InsertLinkAsync(
                connection, sourceRecordId, contentId, fileName, caption,
                sequenceNo, normalizedType, sourceUrl, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new AddAttachmentResult(
                linkId, contentId, contentHash, byteLength, mimeType, deduplicated);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads one attachment (bytes + metadata) by its link id, or null if the
    /// link does not exist. The returned bytes are byte-identical to what was
    /// stored.
    /// </summary>
    public async Task<AttachmentBytes?> GetAttachmentAsync(
        long sourceRecordAttachmentId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT l.source_record_attachment_id, c.attachment_content_id,
                   c.content, l.file_name, l.caption, c.mime_type,
                   c.byte_length, c.content_hash, c.created_at
            FROM research.source_record_attachment l
            JOIN research.attachment_content c
                ON c.attachment_content_id = l.attachment_content_id
            WHERE l.source_record_attachment_id = @id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = sourceRecordAttachmentId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AttachmentBytes(
            SourceRecordAttachmentId: reader.GetFieldValue<long>(0),
            AttachmentContentId: reader.GetFieldValue<long>(1),
            Content: reader.GetFieldValue<byte[]>(2),
            FileName: reader.IsDBNull(3) ? null : reader.GetFieldValue<string>(3),
            Caption: reader.IsDBNull(4) ? null : reader.GetFieldValue<string>(4),
            MimeType: reader.GetFieldValue<string>(5),
            ByteLength: reader.GetFieldValue<long>(6),
            ContentHash: reader.GetFieldValue<string>(7).Trim(),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(8));
    }

    /// <summary>
    /// Lists a source record's attachments (metadata only, no bytes), ordered by
    /// <c>sequence_no</c> then link id.
    /// </summary>
    public async Task<IReadOnlyList<AttachmentInfo>> ListAttachmentsAsync(
        Guid sourceRecordId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT l.source_record_attachment_id, c.attachment_content_id,
                   l.file_name, l.caption, l.sequence_no, l.attachment_type,
                   l.source_url, c.mime_type, c.byte_length, c.content_hash, c.created_at
            FROM research.source_record_attachment l
            JOIN research.attachment_content c
                ON c.attachment_content_id = l.attachment_content_id
            WHERE l.source_record_id = @source_record_id
            ORDER BY l.sequence_no, l.source_record_attachment_id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<AttachmentInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AttachmentInfo(
                SourceRecordAttachmentId: reader.GetFieldValue<long>(0),
                AttachmentContentId: reader.GetFieldValue<long>(1),
                FileName: reader.IsDBNull(2) ? null : reader.GetFieldValue<string>(2),
                Caption: reader.IsDBNull(3) ? null : reader.GetFieldValue<string>(3),
                SequenceNo: reader.GetFieldValue<int>(4),
                AttachmentType: reader.GetFieldValue<string>(5),
                SourceUrl: reader.IsDBNull(6) ? null : reader.GetFieldValue<string>(6),
                MimeType: reader.GetFieldValue<string>(7),
                ByteLength: reader.GetFieldValue<long>(8),
                ContentHash: reader.GetFieldValue<string>(9).Trim(),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return results;
    }

    /// <summary>
    /// Deletes ONLY the link row. The shared <c>attachment_content</c> is never
    /// touched here (it may be referenced by other records); reclaiming
    /// unreferenced content is the separate, explicit
    /// <see cref="CleanupOrphanedContentAsync"/> operation. Returns whether a
    /// link row was deleted.
    /// </summary>
    public async Task<bool> DeleteAttachmentLinkAsync(
        long sourceRecordAttachmentId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM research.source_record_attachment
            WHERE source_record_attachment_id = @id;
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = sourceRecordAttachmentId });

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// Explicit orphan cleanup: deletes every <c>attachment_content</c> row that
    /// no <c>source_record_attachment</c> link references, and returns the number
    /// removed. This is a deliberate, standalone reclamation step — it is NEVER
    /// invoked as a side effect of deleting a link.
    /// </summary>
    public async Task<int> CleanupOrphanedContentAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM research.attachment_content c
            WHERE NOT EXISTS (
                SELECT 1 FROM research.source_record_attachment l
                WHERE l.attachment_content_id = c.attachment_content_id);
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes a base64 payload and enforces <paramref name="maxDecodedBytes"/>
    /// (the base64 channel's decoded ceiling) on the result. For the tool layer
    /// to call before <see cref="AddAttachmentAsync"/>; the repository itself
    /// only ever takes raw <c>byte[]</c>. The base64 STRING length is bounded
    /// first so an oversized payload is rejected before it is fully decoded.
    /// </summary>
    /// <exception cref="AttachmentTooLargeException">Decoded length exceeds <paramref name="maxDecodedBytes"/>.</exception>
    public static byte[] DecodeBase64(string base64, long maxDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(base64);

        // A base64 string of length N decodes to at most (N / 4) * 3 bytes.
        // Reject clearly-oversized input before allocating the decode buffer.
        var maxBase64Chars = ((maxDecodedBytes + 2) / 3) * 4;
        if (base64.Length > maxBase64Chars + 4)
        {
            throw new AttachmentTooLargeException(
                (long)base64.Length * 3 / 4, maxDecodedBytes, "base64 decoded");
        }

        var bytes = Convert.FromBase64String(base64);
        if (bytes.LongLength > maxDecodedBytes)
        {
            throw new AttachmentTooLargeException(bytes.LongLength, maxDecodedBytes, "base64 decoded");
        }

        return bytes;
    }

    private static async Task<long?> FindContentByHashAsync(
        NpgsqlConnection connection, string contentHash, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT attachment_content_id
            FROM research.attachment_content
            WHERE content_hash = @content_hash;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("content_hash", NpgsqlDbType.Char) { Value = contentHash });
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : (long)result;
    }

    private static async Task<bool> IsContentLinkedToRecordAsync(
        NpgsqlConnection connection, Guid sourceRecordId, long attachmentContentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM research.source_record_attachment
                WHERE source_record_id = @source_record_id
                  AND attachment_content_id = @attachment_content_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("attachment_content_id", NpgsqlDbType.Bigint) { Value = attachmentContentId });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<long> SumDistinctRecordBytesAsync(
        NpgsqlConnection connection, Guid sourceRecordId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(SUM(c.byte_length), 0)::bigint
            FROM (
                SELECT DISTINCT l.attachment_content_id
                FROM research.source_record_attachment l
                WHERE l.source_record_id = @source_record_id
            ) d
            JOIN research.attachment_content c
                ON c.attachment_content_id = d.attachment_content_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<long> InsertContentAsync(
        NpgsqlConnection connection, string contentHash, byte[] content, long byteLength,
        string mimeType, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO research.attachment_content (content_hash, content, byte_length, mime_type)
            VALUES (@content_hash, @content, @byte_length, @mime_type)
            RETURNING attachment_content_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("content_hash", NpgsqlDbType.Char) { Value = contentHash });
        command.Parameters.Add(new NpgsqlParameter("content", NpgsqlDbType.Bytea) { Value = content });
        command.Parameters.Add(new NpgsqlParameter("byte_length", NpgsqlDbType.Bigint) { Value = byteLength });
        command.Parameters.Add(new NpgsqlParameter("mime_type", NpgsqlDbType.Text) { Value = mimeType });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<long> InsertLinkAsync(
        NpgsqlConnection connection, Guid sourceRecordId, long attachmentContentId,
        string? fileName, string? caption, int sequenceNo, string attachmentType,
        string? sourceUrl, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO research.source_record_attachment
                (source_record_id, attachment_content_id, file_name, caption,
                 sequence_no, attachment_type, source_url)
            VALUES
                (@source_record_id, @attachment_content_id, @file_name, @caption,
                 @sequence_no, @attachment_type, @source_url)
            RETURNING source_record_attachment_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
        command.Parameters.Add(new NpgsqlParameter("attachment_content_id", NpgsqlDbType.Bigint) { Value = attachmentContentId });
        command.Parameters.Add(new NpgsqlParameter("file_name", NpgsqlDbType.Text) { Value = (object?)fileName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("caption", NpgsqlDbType.Text) { Value = (object?)caption ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("sequence_no", NpgsqlDbType.Integer) { Value = sequenceNo });
        command.Parameters.Add(new NpgsqlParameter("attachment_type", NpgsqlDbType.Text) { Value = attachmentType });
        command.Parameters.Add(new NpgsqlParameter("source_url", NpgsqlDbType.Text) { Value = (object?)sourceUrl ?? DBNull.Value });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Maps the caller's attachment-type hint to the schema's CHECK values
    /// ('image', 'document', 'other'), defaulting to 'other' for anything
    /// unrecognized. This is only a categorization label; it is NOT the trusted
    /// content type (that comes from <see cref="MimeSniffer"/>).
    /// </summary>
    private static string NormalizeAttachmentType(string? attachmentType) =>
        attachmentType?.Trim().ToLowerInvariant() switch
        {
            "image" => "image",
            "document" => "document",
            _ => "other",
        };
}
