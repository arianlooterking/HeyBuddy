# Hidden WPF routing integration tests

Runs the actual `MainWindow` routing methods and application services with a scripted loopback SSE server, separate temporary data folders, and a synthetic app-catalog registration pointing to a nonexistent executable. Direct commands are cancelled through the common runner before dispatch. No windows are shown, app processes launched, global shortcuts installed, microphone audio captured, or models loaded.

```powershell
dotnet run --project tests/Clicky.Routing.UiTests -- artifacts/routing-ui
```

Coverage includes default Auto direct launch before model-provider creation, background Agent mode, Chat only without tool execution, ordinary Auto questions, the actual composer PreviewKeyDown Enter event, session changes during cancellation, and persisted privacy markers. The Enter check uses a routed WPF event; it does not simulate physical Shift+Enter or modify global keyboard state. `results.json` records individual checks and errors. A failing test returns exit code 1. The fixture uses reflection to reach existing private routing methods and the existing app-catalog discovery delegate; production interfaces are unchanged. It loads the shipped style resources into a plain WPF Application, so production App startup cannot run.
