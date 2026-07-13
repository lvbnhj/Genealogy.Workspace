namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Minimal KEY=VALUE parser for the workspace's <c>.env</c> file. Only
/// understands the subset used by <c>Genealogy.Workspace/.env.example</c>:
/// one assignment per line, <c>#</c>-prefixed comment lines, blank lines
/// ignored, and no quoting/escaping.
/// </summary>
internal static class DotEnvFile
{
    public static IReadOnlyDictionary<string, string> Parse(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = value;
        }

        return values;
    }
}
