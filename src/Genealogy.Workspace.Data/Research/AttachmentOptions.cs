namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Size limits for the binary attachment pipeline (plan §8/§13, Phase 6).
/// Populated from environment variables via <see cref="FromEnvironment"/>,
/// mirroring <see cref="WorkspaceDbOptions"/>: each value falls back to a
/// documented default and an unparseable value is captured so
/// <see cref="Validate"/> can report it with the offending text rather than
/// silently substituting a default.
/// </summary>
public sealed class AttachmentOptions
{
    /// <summary>Default per-file ceiling: 20 MB.</summary>
    public const long DefaultMaxFileBytes = 20L * 1024 * 1024;

    /// <summary>Default per-record aggregate ceiling: 100 MB.</summary>
    public const long DefaultMaxRecordBytes = 100L * 1024 * 1024;

    /// <summary>Default decoded-base64 ceiling: 8 MB.</summary>
    public const long DefaultMaxBase64Bytes = 8L * 1024 * 1024;

    /// <summary>
    /// Maximum size of a single attachment's raw bytes (env
    /// <c>GENEALOGY_ATTACHMENT_MAX_BYTES</c>). Bounds a local-path import;
    /// enforced in <c>AttachmentRepository.AddAttachmentAsync</c> step (a).
    /// </summary>
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    /// <summary>
    /// Maximum total bytes of the DISTINCT content linked to any one source
    /// record (env <c>GENEALOGY_ATTACHMENT_MAX_RECORD_BYTES</c>). Enforced in
    /// <c>AttachmentRepository.AddAttachmentAsync</c> step (d).
    /// </summary>
    public long MaxRecordBytes { get; set; } = DefaultMaxRecordBytes;

    /// <summary>
    /// Maximum DECODED length accepted when the source is a base64 payload
    /// (env <c>GENEALOGY_ATTACHMENT_MAX_BASE64_BYTES</c>). Base64 rides the MCP
    /// JSON-RPC channel, so it gets a tighter ceiling than a local-path import;
    /// enforced in <c>AttachmentRepository.DecodeBase64</c>.
    /// </summary>
    public long MaxBase64Bytes { get; set; } = DefaultMaxBase64Bytes;

    /// <summary>
    /// Builds options from GENEALOGY_ATTACHMENT_MAX_BYTES /
    /// _MAX_RECORD_BYTES / _MAX_BASE64_BYTES. Missing values fall back to the
    /// documented defaults; an unparseable value is left as an obviously invalid
    /// -1 so <see cref="Validate"/> surfaces it with the offending text.
    /// </summary>
    public static AttachmentOptions FromEnvironment()
    {
        var options = new AttachmentOptions();

        ReadLongOrDefault(
            "GENEALOGY_ATTACHMENT_MAX_BYTES", DefaultMaxFileBytes,
            v => options.MaxFileBytes = v,
            raw => options.InvalidMaxFileBytesText = raw);

        ReadLongOrDefault(
            "GENEALOGY_ATTACHMENT_MAX_RECORD_BYTES", DefaultMaxRecordBytes,
            v => options.MaxRecordBytes = v,
            raw => options.InvalidMaxRecordBytesText = raw);

        ReadLongOrDefault(
            "GENEALOGY_ATTACHMENT_MAX_BASE64_BYTES", DefaultMaxBase64Bytes,
            v => options.MaxBase64Bytes = v,
            raw => options.InvalidMaxBase64BytesText = raw);

        return options;
    }

    /// <summary>Raw env text when GENEALOGY_ATTACHMENT_MAX_BYTES could not be parsed.</summary>
    internal string? InvalidMaxFileBytesText { get; set; }

    /// <summary>Raw env text when GENEALOGY_ATTACHMENT_MAX_RECORD_BYTES could not be parsed.</summary>
    internal string? InvalidMaxRecordBytesText { get; set; }

    /// <summary>Raw env text when GENEALOGY_ATTACHMENT_MAX_BASE64_BYTES could not be parsed.</summary>
    internal string? InvalidMaxBase64BytesText { get; set; }

    /// <summary>
    /// Validates all limits and throws a single <see cref="InvalidOperationException"/>
    /// listing every problem found. Each limit must parse and be positive.
    /// </summary>
    public void Validate()
    {
        var problems = new List<string>();

        CheckLimit("GENEALOGY_ATTACHMENT_MAX_BYTES", MaxFileBytes, InvalidMaxFileBytesText, problems);
        CheckLimit("GENEALOGY_ATTACHMENT_MAX_RECORD_BYTES", MaxRecordBytes, InvalidMaxRecordBytesText, problems);
        CheckLimit("GENEALOGY_ATTACHMENT_MAX_BASE64_BYTES", MaxBase64Bytes, InvalidMaxBase64BytesText, problems);

        if (problems.Count > 0)
        {
            var message = "Invalid attachment configuration:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
            throw new InvalidOperationException(message);
        }
    }

    private static void CheckLimit(string variable, long value, string? invalidText, List<string> problems)
    {
        if (invalidText is not null)
        {
            problems.Add($"'{invalidText}' is not a valid integer ({variable}).");
        }
        else if (value <= 0)
        {
            problems.Add($"Must be a positive number of bytes but was {value} ({variable}).");
        }
    }

    private static void ReadLongOrDefault(
        string variable, long fallback, Action<long> assign, Action<string> captureInvalid)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            assign(fallback);
        }
        else if (long.TryParse(raw, out var parsed))
        {
            assign(parsed);
        }
        else
        {
            // Leave an obviously invalid value so Validate() surfaces it with
            // the offending text rather than silently falling back to a default.
            assign(-1);
            captureInvalid(raw);
        }
    }
}
