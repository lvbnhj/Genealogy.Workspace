using Genealogy.Workspace.Data;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Unit-level negative-configuration test: no database or container is
/// required. Confirms <see cref="WorkspaceDbOptions.Validate"/> rejects a
/// missing password and names the offending environment variable.
/// </summary>
public sealed class WorkspaceDbOptionsValidationTests
{
    [Fact]
    public void Validate_WithEmptyPassword_ThrowsMentioningPasswordEnvironmentVariable()
    {
        var options = new WorkspaceDbOptions
        {
            Host = WorkspaceDbOptions.DefaultHost,
            Port = WorkspaceDbOptions.DefaultPort,
            Database = WorkspaceDbOptions.DefaultDatabase,
            Username = WorkspaceDbOptions.DefaultUsername,
            Password = string.Empty,
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("GENEALOGY_DB_PASSWORD", exception.Message, StringComparison.Ordinal);
    }
}
