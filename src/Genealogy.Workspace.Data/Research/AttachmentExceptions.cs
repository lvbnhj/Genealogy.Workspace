namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Thrown when an attachment's byte count exceeds the configured limit — either
/// the per-file ceiling (<see cref="AttachmentOptions.MaxFileBytes"/>) or, for a
/// base64 payload, the decoded ceiling
/// (<see cref="AttachmentOptions.MaxBase64Bytes"/>).
/// </summary>
public sealed class AttachmentTooLargeException : Exception
{
    public AttachmentTooLargeException(long actualBytes, long limitBytes, string limitName)
        : base($"Attachment is {actualBytes} bytes, exceeding the {limitName} limit of {limitBytes} bytes.")
    {
        ActualBytes = actualBytes;
        LimitBytes = limitBytes;
        LimitName = limitName;
    }

    /// <summary>Actual size that was rejected.</summary>
    public long ActualBytes { get; }

    /// <summary>Configured ceiling that was exceeded.</summary>
    public long LimitBytes { get; }

    /// <summary>Human-readable name of the limit (e.g. "per-file", "base64 decoded").</summary>
    public string LimitName { get; }
}

/// <summary>
/// Thrown when an attachment's magic bytes are not in the
/// <see cref="MimeSniffer"/> allowlist. The client's declared type/extension is
/// never trusted, so this reflects the server's own classification of the bytes.
/// </summary>
public sealed class AttachmentTypeNotAllowedException : Exception
{
    public AttachmentTypeNotAllowedException()
        : base("Attachment content type is not allowed. The server accepts only PNG, JPEG, GIF, WEBP, TIFF and PDF, detected from the file's magic bytes.")
    {
    }
}

/// <summary>
/// Thrown when adding an attachment would push a source record's total DISTINCT
/// content past <see cref="AttachmentOptions.MaxRecordBytes"/>.
/// </summary>
public sealed class RecordAttachmentQuotaExceededException : Exception
{
    public RecordAttachmentQuotaExceededException(
        Guid sourceRecordId, long currentBytes, long additionalBytes, long limitBytes)
        : base($"Source record {sourceRecordId} already holds {currentBytes} bytes of attachments; " +
               $"adding {additionalBytes} bytes would exceed the per-record limit of {limitBytes} bytes.")
    {
        SourceRecordId = sourceRecordId;
        CurrentBytes = currentBytes;
        AdditionalBytes = additionalBytes;
        LimitBytes = limitBytes;
    }

    /// <summary>The record whose quota would be exceeded.</summary>
    public Guid SourceRecordId { get; }

    /// <summary>Bytes of DISTINCT content already linked to the record.</summary>
    public long CurrentBytes { get; }

    /// <summary>Bytes the new attachment would add (0 if already linked/deduped to this record).</summary>
    public long AdditionalBytes { get; }

    /// <summary>Configured per-record ceiling.</summary>
    public long LimitBytes { get; }
}
