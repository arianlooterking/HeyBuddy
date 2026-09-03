using System.Text.Json;

namespace Clicky.Connectors;

public enum ConnectorTransport
{
    Http, Stdio, Google, Spotify, PublicApi, LocalFiles, Unsupported
}
public enum ConnectorAuthMode
{
    None, OAuth, Bearer, ApiKey
}
public enum ConnectorStatus
{
    Implemented, NeedsConfiguration, Configured, Connecting, Connected, Verified, Failed, Unsupported
}

public sealed record ConnectorCatalogEntry(string Id, string Name, string Group, ConnectorTransport Transport,
    string Endpoint, ConnectorAuthMode AuthMode, string Description, string SetupInstructions,
    string DocumentationUrl, string[] DefaultScopes, string? TestTool = null, string? TestArguments = null,
    bool Supported = true);

public sealed class ConnectorConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string CatalogId { get; set; } = "custom-mcp";
    public string Name { get; set; } = "Custom MCP";
    public ConnectorTransport Transport
    {
        get; set;
    }
    public string Endpoint { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public List<string> SecretEnvironmentNames { get; set; } = [];
    public bool Enabled
    {
        get; set;
    }
    public ConnectorAuthMode AuthMode
    {
        get; set;
    }
    public string ClientId { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    public int CallbackPort { get; set; } = 49172;
    public int TimeoutSeconds { get; set; } = 60;
    public string LocalPath { get; set; } = "";
    public string Account { get; set; } = "";
    public DateTimeOffset? LastVerifiedAt
    {
        get; set;
    }
    public string LastTestMessage { get; set; } = "Not tested";
    public List<string> DisabledTools { get; set; } = [];

    public static ConnectorConfiguration FromCatalog(ConnectorCatalogEntry entry) => new()
    {
        CatalogId = entry.Id,
        Name = entry.Name,
        Transport = entry.Transport,
        Endpoint = entry.Endpoint,
        AuthMode = entry.AuthMode,
        Scopes = [.. entry.DefaultScopes]
    };

    internal ConnectorConfiguration Copy() => JsonSerializer.Deserialize<ConnectorConfiguration>(JsonSerializer.Serialize(this))!;
}

public sealed record ConnectorTestResult(string ConnectorId, bool Success, ConnectorStatus Status,
    string Message, int ToolCount = 0, string? Account = null, DateTimeOffset? VerifiedAt = null);

public sealed record ConnectorToolPreview(string ConnectorId, string ConnectorName, string ToolName,
    string Target, string Arguments, string Risk, string Notice);
