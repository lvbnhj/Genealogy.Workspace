using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Genealogy.Workspace.Data.Configuration;

/// <summary>
/// Registers the workspace's stdio MCP entry point without discarding unrelated
/// client configuration. This lives in the Data assembly so both the source
/// installer and the prebuilt runtime Migrator can use the same implementation.
/// </summary>
public static partial class McpClientConfigRegistrar
{
    public const string McpJsonClient = "mcp-json";
    public const string CodexClient = "codex";

    public static void Register(string client, string configPath, string serverName, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!ServerNamePattern().IsMatch(serverName))
        {
            throw new ArgumentException(
                "Server name may contain only letters, digits, underscores, and hyphens.",
                nameof(serverName));
        }

        var absoluteConfigPath = Path.GetFullPath(configPath);
        var absoluteCommand = Path.GetFullPath(command);

        switch (client.Trim().ToLowerInvariant())
        {
            case McpJsonClient:
                RegisterMcpJson(absoluteConfigPath, serverName, absoluteCommand);
                break;
            case CodexClient:
                RegisterCodexToml(absoluteConfigPath, serverName, absoluteCommand);
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported client '{client}'. Expected '{McpJsonClient}' or '{CodexClient}'.",
                    nameof(client));
        }
    }

    public static void RegisterMcpJson(string configPath, string serverName, string command)
    {
        JsonObject root;
        if (File.Exists(configPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(configPath)))
        {
            root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                ?? throw new InvalidDataException($"MCP config must contain a JSON object: {configPath}");
        }
        else
        {
            root = new JsonObject();
        }

        JsonObject servers;
        if (root["mcpServers"] is null)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        else
        {
            servers = root["mcpServers"] as JsonObject
                ?? throw new InvalidDataException($"'mcpServers' must be a JSON object: {configPath}");
        }

        servers[serverName] = new JsonObject
        {
            ["command"] = Path.GetFullPath(command),
            ["args"] = new JsonArray(),
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        WriteAtomically(configPath, json);
    }

    public static void RegisterCodexToml(string configPath, string serverName, string command)
    {
        var original = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        if (InlineMcpServersPattern().IsMatch(original))
        {
            throw new InvalidDataException(
                $"Cannot safely update inline 'mcp_servers = {{ ... }}' configuration in {configPath}. " +
                "Convert it to [mcp_servers.<name>] tables first.");
        }

        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var targetHeaders = new HashSet<string>(StringComparer.Ordinal)
        {
            $"[mcp_servers.{serverName}]",
            $"[mcp_servers.\"{serverName}\"]",
        };

        var result = new List<string>(lines.Count + 6);
        var skippingTarget = false;
        foreach (var line in lines)
        {
            var trimmed = StripTomlComment(line).Trim();
            if (targetHeaders.Contains(trimmed))
            {
                skippingTarget = true;
                continue;
            }

            if (skippingTarget)
            {
                if (!TomlTableHeaderPattern().IsMatch(trimmed))
                {
                    continue;
                }

                skippingTarget = false;
            }

            result.Add(line);
        }

        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        if (result.Count > 0)
        {
            result.Add(string.Empty);
        }

        result.Add($"[mcp_servers.{serverName}]");
        result.Add("enabled = true");
        result.Add($"command = \"{EscapeTomlBasicString(Path.GetFullPath(command))}\"");
        result.Add("args = []");
        result.Add(string.Empty);

        WriteAtomically(configPath, string.Join('\n', result));
    }

    private static string StripTomlComment(string line)
    {
        var inBasicString = false;
        var inLiteralString = false;
        var escaped = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (inBasicString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inBasicString = false;
                }
            }
            else if (inLiteralString)
            {
                if (character == '\'')
                {
                    inLiteralString = false;
                }
            }
            else if (character == '"')
            {
                inBasicString = true;
            }
            else if (character == '\'')
            {
                inLiteralString = true;
            }
            else if (character == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string EscapeTomlBasicString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Could not resolve parent directory for {path}.");
        Directory.CreateDirectory(directory);

        UnixFileMode? existingMode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            existingMode = File.GetUnixFileMode(path);
        }

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            if (!OperatingSystem.IsWindows() && existingMode is not null)
            {
                File.SetUnixFileMode(tempPath, existingMode.Value);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerNamePattern();

    [GeneratedRegex("(?m)^\\s*mcp_servers\\s*=")]
    private static partial Regex InlineMcpServersPattern();

    [GeneratedRegex("^\\[\\[?.+\\]\\]?$", RegexOptions.CultureInvariant)]
    private static partial Regex TomlTableHeaderPattern();
}
