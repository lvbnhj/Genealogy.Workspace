using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Deserialized view of <c>staging_manifest.json</c> as emitted by
/// <c>gedcom_tool.py export-staging-tsv</c> (see
/// <c>Genealogy.Workspace/tools/gedcom/gedcom_tool.py</c>,
/// <c>run_export_staging_tsv</c>). The manifest describes one import batch: the
/// source GEDCOM, the target tree, the resolved root person, per-file row counts
/// and the TSV load order.
/// </summary>
public sealed record GedcomStagingManifest
{
    /// <summary>The batch identifier every staged row is keyed by.</summary>
    [JsonPropertyName("batchId")]
    public Guid BatchId { get; init; }

    /// <summary>Absolute path of the GEDCOM the artifacts were exported from.</summary>
    [JsonPropertyName("sourceFilePath")]
    public string SourceFilePath { get; init; } = string.Empty;

    /// <summary>SHA-256 of the source GEDCOM, or null when not computed.</summary>
    [JsonPropertyName("sourceFileHash")]
    public string? SourceFileHash { get; init; }

    /// <summary>The tree these artifacts belong to.</summary>
    [JsonPropertyName("treeId")]
    public Guid TreeId { get; init; }

    /// <summary>Optional human-readable tree name passed to the exporter.</summary>
    [JsonPropertyName("treeName")]
    public string? TreeName { get; init; }

    /// <summary><c>"legacy"</c> or <c>"tree-scoped"</c> UUID derivation mode.</summary>
    [JsonPropertyName("uuidMode")]
    public string? UuidMode { get; init; }

    /// <summary>The GEDCOM xref of the resolved root person, if one was requested.</summary>
    [JsonPropertyName("rootExternalId")]
    public string? RootExternalId { get; init; }

    /// <summary>The derived tree-person UUID of the root, if one was requested.</summary>
    [JsonPropertyName("rootTreePersonId")]
    public Guid? RootTreePersonId { get; init; }

    /// <summary>Row count per emitted TSV, keyed by file name.</summary>
    [JsonPropertyName("counts")]
    public IReadOnlyDictionary<string, int> Counts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Declared byte encoding of the TSV artifacts (expected <c>"utf-16"</c>).</summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    /// <summary>
    /// TSV file names in a foreign-key-safe load order. Retained for reference;
    /// the loader drives its own dependency-ordered table list.
    /// </summary>
    [JsonPropertyName("loadOrder")]
    public IReadOnlyList<string> LoadOrder { get; init; } = [];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Deserializes a manifest from its JSON text.</summary>
    public static GedcomStagingManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<GedcomStagingManifest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("staging_manifest.json deserialized to null.");
    }

    /// <summary>Reads and deserializes <c>staging_manifest.json</c> from a file.</summary>
    public static async Task<GedcomStagingManifest> LoadAsync(
        string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Returns the row count for a TSV file name, or 0 when absent.</summary>
    public int CountFor(string fileName) =>
        Counts.TryGetValue(fileName, out var count) ? count : 0;
}
