using System.Text.Json.Nodes;
using Genealogy.Workspace.Data.Configuration;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

public sealed class McpClientConfigRegistrarTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"genealogy-mcp-config-{Guid.NewGuid():N}");

    public McpClientConfigRegistrarTests() => Directory.CreateDirectory(_tempDirectory);

    [Fact]
    public void RegisterMcpJsonPreservesOtherServersAndSettings()
    {
        var configPath = Path.Combine(_tempDirectory, ".mcp.json");
        var commandPath = Path.Combine(_tempDirectory, "run-mcp.sh");
        File.WriteAllText(configPath,
            """
            {
              "customSetting": true,
              "mcpServers": {
                "other": { "command": "/opt/other", "args": ["serve"] },
                "genealogy-workspace": { "command": "/old/path", "args": [] }
              }
            }
            """);

        McpClientConfigRegistrar.RegisterMcpJson(
            configPath, "genealogy-workspace", commandPath);

        var root = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        Assert.True(root["customSetting"]!.GetValue<bool>());
        Assert.Equal("/opt/other", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(commandPath),
            root["mcpServers"]!["genealogy-workspace"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RegisterCodexTomlReplacesOnlyTheTargetTable()
    {
        var configPath = Path.Combine(_tempDirectory, "config.toml");
        var commandPath = Path.Combine(_tempDirectory, "runtime with spaces", "run-mcp.sh");
        File.WriteAllText(configPath,
            """
            model = "gpt-example"

            [mcp_servers.other]
            command = "/opt/other"
            args = ["serve"]

            [mcp_servers.genealogy-workspace] # old managed value
            command = "/old/path"
            args = []

            [features]
            example = true
            """);

        McpClientConfigRegistrar.RegisterCodexToml(
            configPath, "genealogy-workspace", commandPath);

        var result = File.ReadAllText(configPath);
        Assert.Contains("model = \"gpt-example\"", result, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", result, StringComparison.Ordinal);
        Assert.Contains("[features]", result, StringComparison.Ordinal);
        Assert.Contains($"command = \"{Path.GetFullPath(commandPath)}\"", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "[mcp_servers.genealogy-workspace]"));
        Assert.DoesNotContain("/old/path", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterCodexTomlRejectsInlineMcpServerConfiguration()
    {
        var configPath = Path.Combine(_tempDirectory, "config.toml");
        File.WriteAllText(configPath, "mcp_servers = { other = { command = \"server\" } }\n");

        var error = Assert.Throws<InvalidDataException>(() =>
            McpClientConfigRegistrar.RegisterCodexToml(
                configPath, "genealogy-workspace", Path.Combine(_tempDirectory, "run-mcp.sh")));

        Assert.Contains("inline", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
