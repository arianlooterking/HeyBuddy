# Companion sizing regression check

From the repository root:

```powershell
dotnet run --project scripts/cursor-smoke/CursorSmoke.csproj --configuration Release -- artifacts/cursor-smoke
```

This independent WPF executable compiles the actual companion and settings sources. It checks exact half-size geometry, raster bounds at 96/144/192 DPI, readable reply text, compact idle bounds, and immediate menu/save/event behavior. It writes PNGs and `results.json` to the output directory. Settings saves are redirected to its isolated `settings-fixture` subdirectory.

No windows are shown, no native input is sent, and the running app is untouched. The check also renders the compact listening state and verifies that its three bars react to supplied microphone levels. The shell's text-language helper is substituted because language detection is outside this sizing check. This does not claim a live multi-monitor, microphone, or screen-reader test.
