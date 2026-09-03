namespace Clicky.Connectors;

public static class ConnectorCatalog
{
    private static readonly string[] Empty = [];
    private const string GoogleSetup = "Create your own Google Cloud OAuth Desktop client, enable this product's API, add yourself as a test user if the app is in testing, enter its client ID (and client secret when Google supplies one), then authorize. Read scopes are the default; additional write scopes need explicit consent.";
    public static IReadOnlyList<ConnectorCatalogEntry> Entries { get; } = Build();

    private static ConnectorCatalogEntry[] Build() =>
    [
        Google("gmail", "Gmail", "https://gmail.googleapis.com/gmail/v1/", "https://www.googleapis.com/auth/gmail.readonly", "Read mail, inspect threads, draft and send messages with approval and extra scopes.", "https://developers.google.com/workspace/gmail/api/guides"),
        Google("drive", "Google Drive", "https://www.googleapis.com/drive/v3/", "https://www.googleapis.com/auth/drive.readonly", "Find and read files; create text files with write consent.", "https://developers.google.com/workspace/drive/api/guides/about-sdk"),
        Google("docs", "Google Docs", "https://docs.googleapis.com/v1/", "https://www.googleapis.com/auth/documents.readonly", "Read documents, create documents and apply batches with write consent.", "https://developers.google.com/workspace/docs/api/reference/rest"),
        Google("sheets", "Google Sheets", "https://sheets.googleapis.com/v4/", "https://www.googleapis.com/auth/spreadsheets.readonly", "Read spreadsheets and cells; update ranges with write consent.", "https://developers.google.com/workspace/sheets/api/reference/rest"),
        Google("slides", "Google Slides", "https://slides.googleapis.com/v1/", "https://www.googleapis.com/auth/presentations.readonly", "Read presentations; create and update slides with write consent.", "https://developers.google.com/workspace/slides/api/reference/rest"),
        Google("calendar", "Google Calendar", "https://www.googleapis.com/calendar/v3/", "https://www.googleapis.com/auth/calendar.readonly", "Read calendars and events; create events with approval and write consent.", "https://developers.google.com/workspace/calendar/api/v3/reference"),
        Google("contacts", "Google Contacts", "https://people.googleapis.com/v1/", "https://www.googleapis.com/auth/contacts.readonly", "Read and search contacts; create contacts with approval and write consent.", "https://developers.google.com/people/api/rest"),
        Google("google-chat", "Google Chat", "https://chat.googleapis.com/v1/", "https://www.googleapis.com/auth/chat.spaces.readonly", "Read spaces and messages; send messages with approval and extra scopes. Workspace policy may restrict access.", "https://developers.google.com/workspace/chat/api/reference/rest"),
        Mcp("notion", "Notion", "Workspaces", "https://mcp.notion.com/mcp", "OAuth sign-in; workspace access follows the granted account permissions.", "https://developers.notion.com/guides/mcp/get-started-with-mcp", "notion-get-self"),
        Mcp("slack", "Slack", "Workspaces", "https://mcp.slack.com/mcp", "Requires your own registered internal or directory-published Slack app client ID and workspace approval. Dynamic client registration is unsupported.", "https://docs.slack.dev/ai/slack-mcp-server", null),
        Mcp("linear", "Linear", "Workspaces", "https://mcp.linear.app/mcp", "OAuth with dynamic registration; choose read scope for read-only access.", "https://linear.app/docs/mcp", "list_teams", "{}", ["read"]),
        Mcp("airtable", "Airtable", "Workspaces", "https://mcp.airtable.com/mcp", "OAuth or a scoped personal access token; grant only needed bases.", "https://support.airtable.com/articles/9897799762-using-the-airtable-mcp-server", "list_bases"),
        Mcp("github", "GitHub", "Development", "https://api.githubcopilot.com/mcp/", "Use a fine-grained personal access token with access to selected repositories. OAuth requires your own supported client registration.", "https://github.com/github/github-mcp-server", "get_me") with { AuthMode = ConnectorAuthMode.Bearer },
        Mcp("supabase", "Supabase", "Development", "https://mcp.supabase.com/mcp?read_only=true", "OAuth sign-in. Add project_ref to restrict access to a development project. SQL remains sensitive even when read-only mode is selected.", "https://supabase.com/docs/guides/ai-tools/mcp", "list_organizations"),
        Mcp("vercel", "Vercel", "Development", "https://mcp.vercel.com", "Vercel restricts OAuth to reviewed clients. HeyBuddy may require client approval; errors are shown without borrowing another app's identity. A custom MCP endpoint is also supported.", "https://vercel.com/docs/ai-tooling/vercel-mcp", "list_teams"),
        new("spotify", "Spotify", "Media", ConnectorTransport.Spotify, "https://api.spotify.com/v1/", ConnectorAuthMode.OAuth,
            "Search music, inspect playback and playlists, and control playback after confirmation.", "Create your own Spotify app and register http://127.0.0.1:49172/callback/ exactly. Enter its client ID. Spotify's Web API requires Premium; development app restrictions apply.", "https://developer.spotify.com/documentation/web-api", ["user-read-private", "user-read-playback-state", "user-modify-playback-state", "playlist-read-private"]),
        Google("youtube", "YouTube", "https://www.googleapis.com/youtube/v3/", "https://www.googleapis.com/auth/youtube.readonly", "Search videos and read channel details through the YouTube Data API; quotas apply.", "https://developers.google.com/youtube/v3/docs"),
        new("web", "Web research", "Research", ConnectorTransport.PublicApi, "https://en.wikipedia.org/w/api.php", ConnectorAuthMode.None,
            "Source-linked encyclopedia search and public HTTPS webpage reading.", "No account required. Enable and test. Web content is untrusted data.", "https://www.mediawiki.org/wiki/API:Search", Empty),
        new("maps", "Maps", "Research", ConnectorTransport.PublicApi, "https://nominatim.openstreetmap.org/search", ConnectorAuthMode.None,
            "Geocode place names using OpenStreetMap, returning map and attribution links.", "No account required. Respect public usage policy: one request per second; searches are cached.", "https://operations.osmfoundation.org/policies/nominatim/", Empty),
        new("polymarket", "Polymarket research", "Research", ConnectorTransport.PublicApi, "https://gamma-api.polymarket.com/", ConnectorAuthMode.None,
            "Read market/event information. No trading, wallet or order tools.", "Public read-only Gamma API. Enable and test.", "https://docs.polymarket.com/developers/gamma-markets-api/overview", Empty),
        new("obsidian", "Obsidian vault", "Local apps", ConnectorTransport.LocalFiles, "", ConnectorAuthMode.None,
            "Search and read Markdown notes within your selected vault.", "Select your existing vault folder. Writes stay in HeyBuddy's document workflow; this connector is read-only.", "https://help.obsidian.md/Files+and+folders/Manage+vaults", Empty),
        Local("office", "Office / PowerPoint", "Use HeyBuddy's local document reading and generation. For app automation, configure a reviewed local MCP executable; Microsoft Office must be installed for native app control."),
        Local("blender", "Blender", "Configure a reviewed local Blender MCP executable and its matching Blender extension. No community code is installed automatically."),
        Local("excalidraw", "Excalidraw", "Configure a reviewed Excalidraw MCP endpoint or executable. HeyBuddy can also generate .excalidraw JSON through local document creation."),
        Local("codex", "Codex CLI", "If installed, configure the full path to codex with arguments [\"mcp-server\"]. Its own account, tool permissions and sandbox still apply; all advertised tools require approval."),
        Local("claude-code", "Claude Code", "If installed, configure the full path to claude with arguments [\"mcp\",\"serve\"]. Its own account and permissions still apply; all advertised tools require approval."),
        new("custom-mcp", "Custom MCP", "Additional tools", ConnectorTransport.Http, "", ConnectorAuthMode.None,
            "Connect any reviewed remote HTTP or local stdio MCP server.", "Enter a HTTPS endpoint or select Stdio and a trusted executable plus an argument array. Put credentials only in the secret fields. Every unknown tool requires confirmation.", "https://modelcontextprotocol.io/docs/develop/connect-local-servers", Empty),
        Apple("imessage", "iMessage", "Native iMessage automation is unavailable on Windows. No native adapter is claimed."),
        Apple("find-my", "Find My", "Native Find My automation is unavailable on Windows. Use the official iCloud browser experience where supported."),
        Apple("apple-notes", "Apple Notes", "Native Apple Notes automation is unavailable on Windows. The iCloud browser experience may be used through screen assistance.")
    ];

    private static ConnectorCatalogEntry Google(string id, string name, string endpoint, string scope, string description, string docs)
        => new(id, name, "Google", ConnectorTransport.Google, endpoint, ConnectorAuthMode.OAuth, description,
            GoogleSetup, docs, [scope, "openid", "email"]);
    private static ConnectorCatalogEntry Mcp(string id, string name, string group, string endpoint, string setup,
        string docs, string? test, string? args = "{}", string[]? scopes = null)
        => new(id, name, group, ConnectorTransport.Http, endpoint, ConnectorAuthMode.OAuth, "Connect the official MCP server; review discovered tools and account permissions.", setup, docs, scopes ?? Empty, test, args);
    private static ConnectorCatalogEntry Local(string id, string name, string setup)
        => new(id, name, "Local apps", ConnectorTransport.Stdio, "", ConnectorAuthMode.None, "Local MCP bridge; requires a separately installed and reviewed server.", setup, "https://modelcontextprotocol.io/docs/develop/connect-local-servers", Empty);
    private static ConnectorCatalogEntry Apple(string id, string name, string description)
        => new(id, name, "Compatibility", ConnectorTransport.Unsupported, "", ConnectorAuthMode.None, description,
            description, "https://www.icloud.com/", Empty, Supported: false);
}
