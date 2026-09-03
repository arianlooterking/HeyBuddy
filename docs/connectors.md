# HeyBuddy connections

The connection layer is implemented in `Clicky.Connectors` with the official MCP C# SDK 2.2.0. A catalog entry means the integration is available to configure; it does **not** mean your account is connected or that a local bridge is installed.

## Setup and status

Choose an integration, complete its setup fields, enable it, save, then authorize or test. OAuth opens your normal browser and listens only on `127.0.0.1` for three minutes. The default callback is `http://127.0.0.1:49172/callback/`; register that exact URL for Spotify and pre-registered OAuth applications. You can change the port if another application uses it. Google requires a Desktop OAuth client in your own Cloud project, APIs enabled, and your account included in the consent-screen test users when applicable. Desktop OAuth clients may also provide a client secret: put it in the protected secret field.

| State | Meaning |
|---|---|
| Implemented | Catalog support exists; no configuration has been saved. |
| NeedsConfiguration | Required setup fields are missing. |
| Configured | Setup is saved. No active connection is claimed. |
| Connected | MCP negotiation and tool discovery worked. An account/data read is still unverified. |
| Verified | A harmless account or local/public read returned successfully. The result names exactly what was tested. |
| Failed | The test failed; its actionable reason is displayed. |
| Unsupported | A macOS-native feature has no Windows adapter. |

Restarting the application returns saved connections to Configured. A historical verification timestamp remains visible, but a live connection must be tested again before tools become available. Save changes before entering a replacement secret: changing the endpoint, command, argument array, OAuth identity, auth mode, or scopes removes previous credentials to prevent their reuse at another destination.

Credentials go through `ICredentialStore` (Windows implementation uses DPAPI). `connectors.json` under `AppPaths.Root` stores setup metadata and status, with an atomic replacement and `.bak` copy. It contains no bearer, refresh, or client-secret values. Accounts and scope names are inspectable metadata. Do not type secrets into URLs, command arguments, tool arguments, or public names.

## Available adapters

| Integration | Implemented behavior | Prerequisites and limits |
|---|---|---|
| Gmail | List/search mail, read messages/threads/labels; drafts and sends | Your Google OAuth app. Read scope by default. Writes require an additional scope and an exact action approval. |
| Drive | File search, metadata/content reads and export, metadata creation | Download/export output limited to 2 MB; binary content should use HeyBuddy's document workflow. |
| Docs, Sheets, Slides | Read named documents/spreadsheets/presentations; create and API batch updates; spreadsheet range writes | You supply document IDs obtained through Drive or the source URL. Read scope is the default. |
| Calendar | List calendars/events, read event, create/update event | Write scopes and action approval for changes. |
| Contacts | List/search contacts, create contact | Extra scope for changes. |
| Google Chat | List spaces/messages, send message | Workspace policy and separate message scopes may apply. |
| YouTube | Search videos, read video/channel information and playlist items | YouTube Data API enabled; quotas apply. |
| Notion, Linear, Airtable, GitHub, Supabase, Slack, Vercel | Persistent official remote MCP sessions; discovered tool schemas, invocation, and per-tool disabling | Account permissions apply; only exact reviewed read tools at the official route are classified ReadOnly. |
| Spotify | Profile, search, playback/devices, playlists; play/pause/next/previous | Your own registered app, PKCE OAuth, API access/Premium and current development-mode restrictions. Playback changes require approval. |
| Web research | Wikipedia search; public HTTPS page reads with source URLs | No authenticated browsing, scripts or private/local destinations. Maximum 2 MB and 60,000 characters for text. HTTP redirects are not followed automatically. |
| Maps | OpenStreetMap Nominatim place search | One request per second, in-memory query cache, attribution included. Public-service availability and policy apply. |
| Polymarket | Public event/market listing and search | Read only. No trading, wallet or order functionality. |
| Obsidian | Read/search Markdown in a selected vault | Vault boundary enforced; junctions/symlinks excluded. Maximum 1 MB/note, 5,000 scanned files, 30 matches/search. |
| Office, Blender, Excalidraw, Codex, Claude Code | Configuration slots for reviewed local MCP bridges | The bridge/application must already be installed. No community packages are installed automatically. Office reading/generation is also available through the main app's document tools. |
| Custom MCP | Remote HTTPS/loopback HTTP and local stdio | Review the executable/endpoint. Unknown tools always require confirmation. |

Specific provider restrictions are deliberately visible in the catalog:

- Slack requires an app registered for this client and workspace approval; it does not offer dynamic registration. HeyBuddy does not use another client's app ID.
- Vercel restricts OAuth clients to a reviewed list. HeyBuddy may require provider approval; access is not represented as working until the provider accepts the client and a read succeeds.
- GitHub uses your own scoped token by default. Selecting OAuth requires a compatible client registration.
- Supabase defaults to `read_only=true`; add `project_ref` for a development project. Generic SQL is always Sensitive, even if the server advertises it as read-only.
- Apple iMessage, Find My and Notes are explicit compatibility entries. An iCloud browser page is not a native Windows integration.

## Developer contract

Create one `ConnectorService(ICredentialStore, dataDirectory?, openBrowser?, httpClient?)` for the application's lifetime, dispose it on exit, and subscribe to `Changed` with UI-thread dispatch.

- `Catalog` and `ConnectorConfiguration.FromCatalog(entry)` seed the editor.
- `Configurations`, `GetStatus(id)`, `SaveAsync(config)` and `GetConnectorTools(id)` support settings and discovered metadata, including disabled tools. `SetToolAccessAsync(id, disabledNames)` changes permissions without reconnecting a healthy session. Arguments and scopes are arrays. `DisabledTools` contains original server names, not the generated `cx_...` names.
- `SetSecret(id, "token", value)` stores a bearer token. `"client-secret"` stores a registered OAuth client secret. `"env.NAME"` stores the corresponding stdio environment secret; list `NAME` in `SecretEnvironmentNames`.
- `AuthorizeAsync(id)` runs OAuth when needed and then calls `TestAsync`. `TestAsync` negotiates/discovers MCP and invokes only a reviewed harmless account-read probe, or executes a fixed account/public/local read for a direct adapter.
- `RefreshToolsAsync`, `DisconnectAsync`, `RevokeAsync` and `Preview` support lifecycle and review. Disconnect removes executable bindings. Google revocation also attempts remote token revocation. Other providers receive local credential removal; the UI tells the user to revoke the grant in provider settings.
- `IToolExecutor.Tools` contains currently connected, enabled tools. `ExecuteAsync` handles cancellation, a bounded timeout, and one active call per connection. **Always invoke it through the shared AgentRunner approval gate.** The transport service is not a standalone user-consent mechanism.

MCP OAuth uses SDK discovery, PKCE, dynamic registration when supported, issuer/state validation and encrypted token cache/refresh. The callback receiver validates the exact path, host and constant-time state match. Local stdio uses an explicit argument array and a minimal OS environment, adding only secrets selected for that server. Stderr is not copied to logs. New protocol discovery and older initialization handshakes are both supported by the SDK.

Risk classification ignores provider `readOnlyHint` annotations. A curated read-tool set is tied to the exact catalog HTTPS authority and route. All other tools are Sensitive. MCP instructions and tool output are source data; they cannot grant permissions. Google/Spotify write operations have explicit Sensitive metadata and are exposed only when configured scopes include write access. Permission remains subject to the provider's actual granted token scopes.

HTTP API operations use fixed product roots, URL-escaped identifiers and an allowlist of query parameter names. Public-page requests use a fresh validated public IP at connection time, block private addresses, send no cookies or account tokens, and do not follow redirects. There is no arbitrary authenticated HTTP tool.

## Validation evidence

Run `dotnet test tests/Clicky.Connectors.Tests/Clicky.Connectors.Tests.csproj` and `dotnet format src/Clicky.Connectors/Clicky.Connectors.csproj --verify-no-changes`.

The native `Views.ConnectorToolsWindow` provides tool filtering, enable/disable controls, source descriptions/schema details, account/verification snapshots, and protected stdio environment fields. Its constructor accepts the service, a saved configuration and an optional cancellation token. `dotnet run --project tests/Clicky.Connectors.UiTests -- artifacts/connector-ui` runs an isolated Windows UI harness: it exercises persisted disable/re-enable, keeps the verified connection active, checks filtering, saves/clears/removes a synthetic environment secret, and captures desktop/compact renderings. The harness uses only a temporary local vault and never starts the executable named in the environment fixture.

The local suite verifies real loopback MCP HTTP and subprocess stdio exchanges; state mismatch followed by a valid OAuth callback; provider identity risk classification; saved-config/secret separation and credential invalidation; Google authenticated request routing and expired-token refresh with synthetic HTTP responses; vault traversal; cancellation and disconnect behavior; unsafe endpoint rejection.

The automated fixtures use synthetic accounts/tokens and perform no messages, publishing, trading or account changes. No real Google, Spotify, workspace or developer account was authorized during implementation. Provider account access, API quotas and local bridge behavior need verification after the owner connects them. A synthetic test pass is not evidence of third-party account access.

Sources and endpoint provenance are recorded in `src/Clicky.Connectors/source-trust.json`. Review the official source and tool semantics before expanding the read-only allowlist.
