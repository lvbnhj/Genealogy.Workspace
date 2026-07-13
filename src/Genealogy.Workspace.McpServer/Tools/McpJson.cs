using System.Text.Json;

namespace Genealogy.Workspace.McpServer.Tools;

/// <summary>
/// Shared JSON serialization options for every MCP tool's response payload:
/// camelCase property names, indented for readability in tool output.
/// </summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
