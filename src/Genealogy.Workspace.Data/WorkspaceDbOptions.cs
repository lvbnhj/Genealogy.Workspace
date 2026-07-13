namespace Genealogy.Workspace.Data;

/// <summary>
/// Connection settings for the PostgreSQL genealogy workspace.
/// Populated from environment variables via <see cref="FromEnvironment"/>.
/// </summary>
public sealed class WorkspaceDbOptions
{
    /// <summary>Default host when GENEALOGY_DB_HOST is unset.</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>Default port when GENEALOGY_DB_PORT is unset.</summary>
    public const int DefaultPort = 5432;

    /// <summary>Default database when GENEALOGY_DB_DATABASE is unset.</summary>
    public const string DefaultDatabase = "genealogy_workspace";

    /// <summary>Default username when GENEALOGY_DB_USERNAME is unset.</summary>
    public const string DefaultUsername = "genealogy";

    public string Host { get; set; } = DefaultHost;

    public int Port { get; set; } = DefaultPort;

    public string Database { get; set; } = DefaultDatabase;

    public string Username { get; set; } = DefaultUsername;

    /// <summary>
    /// Database password. There is no safe default; a value is required and is
    /// expected to come from the environment (GENEALOGY_DB_PASSWORD).
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Builds options from GENEALOGY_DB_HOST / _PORT / _DATABASE / _USERNAME /
    /// _PASSWORD. Missing values fall back to the documented defaults except the
    /// password, which has no default. An unparseable port is left as-is (0) so
    /// that <see cref="Validate"/> reports it.
    /// </summary>
    public static WorkspaceDbOptions FromEnvironment()
    {
        var options = new WorkspaceDbOptions
        {
            Host = ReadOrDefault("GENEALOGY_DB_HOST", DefaultHost),
            Database = ReadOrDefault("GENEALOGY_DB_DATABASE", DefaultDatabase),
            Username = ReadOrDefault("GENEALOGY_DB_USERNAME", DefaultUsername),
            Password = Environment.GetEnvironmentVariable("GENEALOGY_DB_PASSWORD") ?? string.Empty,
        };

        var rawPort = Environment.GetEnvironmentVariable("GENEALOGY_DB_PORT");
        if (string.IsNullOrWhiteSpace(rawPort))
        {
            options.Port = DefaultPort;
        }
        else if (int.TryParse(rawPort, out var parsedPort))
        {
            options.Port = parsedPort;
        }
        else
        {
            // Leave as an obviously invalid value so Validate() surfaces it with
            // the offending text rather than silently falling back to a default.
            options.Port = 0;
            options.InvalidPortText = rawPort;
        }

        return options;
    }

    /// <summary>
    /// Raw GENEALOGY_DB_PORT text when it could not be parsed as an integer.
    /// Used only to produce a clear validation message.
    /// </summary>
    internal string? InvalidPortText { get; set; }

    /// <summary>
    /// Validates all settings and throws a single <see cref="InvalidOperationException"/>
    /// listing every problem found. The password is required.
    /// </summary>
    public void Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
        {
            problems.Add("Host is required (GENEALOGY_DB_HOST).");
        }

        if (InvalidPortText is not null)
        {
            problems.Add($"Port '{InvalidPortText}' is not a valid integer (GENEALOGY_DB_PORT).");
        }
        else if (Port is < 1 or > 65535)
        {
            problems.Add($"Port must be between 1 and 65535 but was {Port} (GENEALOGY_DB_PORT).");
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            problems.Add("Database is required (GENEALOGY_DB_DATABASE).");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            problems.Add("Username is required (GENEALOGY_DB_USERNAME).");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            problems.Add("Password is required (GENEALOGY_DB_PASSWORD).");
        }

        if (problems.Count > 0)
        {
            var message = "Invalid genealogy workspace database configuration:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
            throw new InvalidOperationException(message);
        }
    }

    private static string ReadOrDefault(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
