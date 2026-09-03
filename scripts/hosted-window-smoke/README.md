# Window identity regressions

This independent Windows/.NET 10 harness links the actual native tool sources and keeps its build outputs separate from the application. It reads existing window/process identity only: no applications are launched, no focus or clipboard changes occur, no input is sent, and no microphone/model/account access is used. It does not prove that clicking, activation or typing works.

From the repository root, run the ordinary-app and stale-cache checks:

```powershell
dotnet run --project scripts/hosted-window-smoke/HostedWindowSmoke.csproj --configuration Release
```

For the packaged-window regression, open Calculator yourself, then opt into its read-only checks:

```powershell
dotnet run --project scripts/hosted-window-smoke/HostedWindowSmoke.csproj --configuration Release -- --calculator
```

Use `--output <directory>` to change the default `artifacts/hosted-window-smoke` evidence location. `results.json` records each passed/failed/skipped check and selected identity metadata; it excludes window titles, executable paths, accessibility text and document contents. Exit code 1 means an assertion or native query failed; 2 means invalid arguments. Exit code 0 can include explicit skips, so inspect `checksSkipped` before treating an environment-dependent check as covered.

The default checks confirm that an ordinary app retains a single host/content identity, matches its actual executable, cannot satisfy another package's identity, and rejects a changed host lifetime or cached content lifetime. If no accessible ordinary window is open, those checks are explicitly skipped. The harness never starts a fixture or app to satisfy them.

`--calculator` first finds a visible window through raw native ownership and process AUMID queries, independently of the mapper under test. It then checks the exact Calculator identity, registered-process matching, rejection of another package, stable issued IDs, and stale cached targets. When Calculator uses ApplicationFrameHost, it additionally checks the actual child relationship and changed host/content PID, start time, child handle and AUMID rejection. If Calculator has no visible identifiable window, it reports a skip. If a future Calculator version owns its window directly, general Calculator identity checks still run and frame-host-specific checks report a skip.

The cache regression changes only this harness's executor instance, calls its resolver, and restores the original value. It never alters another process, window or application data. Reflection intentionally ties this regression to the current private cache/resolver; an internal rename fails clearly so the test can be updated.

The original isolated harness passed 17 actual Calculator checks on September 3, 2026: frame handle `4524930`/host PID `1820`, child handle `2491298`/Calculator PID `18824`, and `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`. These historical handles are not reused by this script. Earlier evidence remains at `artifacts/hosted-window-checks/results.json`. The tracked harness adds reusable discovery, ordinary-app checks and explicit skips; do not count newly added checks as executed until its own results are generated.

Windows API references: [child-window enumeration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumchildwindows), [descendant relationship](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-ischild), and [process application identity](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getapplicationusermodelid).
