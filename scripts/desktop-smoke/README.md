# Native application discovery checks

Run from the repository root:

```powershell
dotnet run --project scripts/desktop-smoke/DesktopSmoke.csproj --configuration Release -- artifacts/desktop-smoke
```

The independent executable compiles the actual native tool sources. Its default run performs read-only installed-app discovery plus deterministic registration, risk, invalid-ID, cancellation and Unicode-packet checks. It never opens an application, changes focus, types, captures audio, or reads Telegram content. `results.json` records the checks and discovered app registrations. The GUI/console PE files written to the artifact directory are inert metadata fixtures and are never executed.

App catalog sources are Windows AppsFolder, Start menu shortcuts and App Paths registry registrations. Shortcuts with arguments, console executables, command interpreters and installer/uninstaller entries are excluded. Packaged apps use their registered activation contract; their internal EXE paths are not directly launched. Duplicate names remain separate choices. Word/Excel/PowerPoint queries do not substitute similarly named utilities when Office is unavailable.

Public APIs on `WindowsDesktopTools`:

- `ListAppsAsync(query, cancellationToken)` returns typed `DesktopApp` choices with `Id`, `Name`, `Source`, `Kind`, `Executable`, and `AppUserModelId`.
- `ExecuteAsync("desktop_launch", { appId }, cancellationToken)` validates a previously returned ID against current registration and file identity. It returns observed process/window evidence. Several existing windows require an explicit choice.
- `ActivateWindowAsync(windowId, cancellationToken)` restores a listed minimized window and checks its final foreground identity. The equivalent tool is `desktop_activate`.
- `desktop_type` retains ValuePattern input and adds bounded visible Unicode input only for explicitly editable TextPattern Edit/Document controls. It checks foreground, exact element focus, held modifiers and cancellation between batches, leaves the clipboard untouched, and reports failed result verification without retrying.

Launch and activation are `LocalWrite`; general input remains `Sensitive`. Real application launch/activation and typing still require a coordinated live test; this read-only harness does not claim those flows passed.

Native API references: [application activation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication), [process app identity](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getapplicationusermodelid), and [Windows foreground restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow).
