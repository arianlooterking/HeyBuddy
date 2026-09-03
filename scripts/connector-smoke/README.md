# Connector live smoke checks

This console harness runs the app's actual `ConnectorService.TestAsync` against public web, maps, and Polymarket endpoints. It records timestamps, elapsed times, truthful service statuses, and errors in JSON. The default output is `artifacts/connector-live/results.json`.

Optional directly installed Codex and Claude executable paths enable MCP stdio initialization and tool discovery. These probes never invoke a CLI tool, sign in, or claim an authenticated account read. The CLIs receive an empty working directory and temporary profile/configuration directories; the connector service uses temporary settings and an in-memory credential store. Existing user settings and accounts are not modified. No executable, package, bridge, or model is downloaded.

```powershell
dotnet run --project scripts/connector-smoke -- --output artifacts/connector-live --codex 'C:\existing\codex.exe' --claude 'C:\existing\claude.exe'
```

Omit either executable to record that CLI as not tested. Each network/transport probe has a bounded timeout, and Ctrl+C cancels the run. Public service rate limits, network restrictions, or unavailable CLI dependencies are recorded as failures. Exit code 0 means all requested checks passed; exit code 1 means at least one check failed or was not tested. Temporary isolated directories are preserved for diagnosis and their path is included in the report; they contain no account credentials.
