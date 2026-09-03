# HeyBuddy desktop execution refinement

The owner reported that everyday tasks fail and features are missing. Local history confirms that the request to open Telegram and navigate to Saved Messages went through plain chat, produced instructions, and created no agent run. This is an execution gap, not a successful task.

Status: release 0.2.0 was built and installed over the existing copy. The upgrade report records a successful install, validates the payload, and confirms that every retained user-data file in that run kept its hash. The installed executable reports file version 0.2.0.0. Post-install checks confirmed the 0.2.0 UI, a ready local model, two verified existing-window activations, and an approval-gated five-action Notepad typing run with executor-verified text. A separate post-run visual reread stopped when user input was detected and is not claimed. A human microphone sample remains pending. Initial findings below describe the starting defects, followed by the evidence gathered while fixing them.

## Findings and implementation order

1. Default conversation has no tools. Make Auto the default, preserve a clearly labeled Chat only mode, and run requested actions through the common permission layer. Keep answers and task progress visible in the conversation.
2. Native tools cannot discover, launch, or activate applications. Add installed-app discovery with stable IDs, verified launching and switching, minimized-window restoration, and explicit ambiguity handling. Simple app-open requests should not require loading a language model.
3. Task completion currently follows model prose even when nothing ran or the last action failed. Require action evidence for delegated tasks; preserve failure/approval/cancellation history and show progress while the model works.
4. General typing requires ValuePattern and refuses many modern editors. Add a guarded foreground fallback for verified editable controls, preserve the clipboard, and distinguish performed input from verified results. Approval must return focus to the approved target without allowing input into a different window.
5. Provider instances accumulate HTTP clients. Reuse correctly keyed clients and measure cold/warm model latency before changing runtime defaults. Preserve checksums, loopback authentication, resource limits, and installed models.
6. Expose installed apps and useful actions in the UI, improve first-use guidance, retain the 50% companion and microphone fixes, and update the feature matrix with actual evidence.

## Validation boundary

Run compiler/analyzer checks, existing tests and targeted regressions, then exercise the built app with real Windows applications. Initial real tasks: open Notepad, Calculator and Telegram; activate a returned window; create/read a local document; type into a new test document; cancel before a subsequent action. No message sending, publishing, account authorization, purchases, destructive changes, or production configuration is part of these tests. Account-dependent integrations remain separate from verified local functionality.

Speech checks must distinguish generated samples from a human microphone test. Performance results must state whether the model was already loaded. Preserve the existing data folder and back up before installing the update.

## Current repository evidence

The latest `scripts/verify.ps1 -NativeFixtures` run completed successfully. Its persisted outputs support the following narrower claims; build/format console output was not saved as a separate log.

| Check | Current evidence and boundary |
|---|---|
| Final build | Release build completed with zero errors and zero warnings; aggregate automated failures were zero. |
| Core | 161/161 tests passed in `artifacts/test-results/verify_net10.0_20260903205808.trx`. |
| Connectors | 12/12 tests passed in `artifacts/test-results/verify_net10.0_20260903205809.trx`. The connector UI fixture also passed 10/10 checks in `artifacts/connector-ui/result.json`; these are protocol/setup fixtures, not authenticated account verification. |
| Runtime | 51/51 tests passed in `artifacts/test-results/verify_net10.0_20260903205807.trx`. No paid provider or account cloud key was used. |
| Conversation routing | 41/41 checks passed in `artifacts/routing-ui/results.json`: Auto/Agent/Chat-only routing, required state-change evidence, Enter submission, privacy markers and cancellation isolation. The harness used a scripted loopback provider, launched no real app, showed no window and performed no inference or microphone capture. |
| Shortcut and color controls | 39/39 checks passed in `artifacts/settings-controls/results.json`: validation, capture-state handling, persistence and rendering. The window/native dialog remained unshown, global hooks were not started and no physical keyboard input was sent. |
| Native speech/input fixture | 19/19 checks passed in `artifacts/native/results.json`, including owned-window UIA input, cancellation, clipboard restoration, local EN/FA/TR synthesis/recognition and speech-asset verification. Audio was generated; this is not a human microphone test. |
| Native render fixture | The sketch composition check passed in `artifacts/native/sketch-result.json`; seven image-preparation checks passed in `image-preparation-results.json`. These are eight render/image checks, separate from the 19 native speech/input checks. |
| Installed-app catalog | 18/18 read-only checks passed in `artifacts/desktop-smoke/results.json`. The snapshot found Notepad, Calculator, VS Code, two Edge registrations and Telegram. Word, Excel and PowerPoint each had zero registered choices, so there is no native Office-app claim. |
| Hosted-window identity | Default run: 8 passed and the optional Calculator group skipped, in `artifacts/hosted-window-smoke/default/results.json`. With the already-open Calculator: 26 passed, zero skipped, in `artifacts/hosted-window-smoke/calculator/results.json`. Both runs record `applicationsLaunched: false`, `foregroundChanged: false` and `inputSent: false`. |
| Main-window rendering | 17/17 diagnostic checks passed in `artifacts/refinement-ui-0.2.0/ui-result.json`: all eight desktop and compact pages plus Persian RTL. `Live` is false; this is layout evidence rather than a user workflow. |
| NuGet vulnerability scan | `artifacts/dependency-audit.json` covers ten projects with `--vulnerable --include-transitive` and records zero vulnerable package entries. |
| Local model context | `artifacts/context-template/before.json` records the original HTTP 500 and two leading system roles. `after.json` records success with one leading system role, 4,698 estimated tokens and zero tool executions. |

## Installed 0.2.0 evidence

The self-contained Windows x64 release is built. The per-user installer, portable ZIP, and `SHA256SUMS.txt` are published together on the GitHub Release. Exact outer-package hashes are intentionally not copied into packaged documentation because changing that documentation changes the package hashes. The release remains unsigned and excludes model downloads.

The final install validation and upgrade each covered the complete SHA-256-manifested payload. `artifacts/release/upgrade-0.2.0.json` records `Success: true`, every installed payload file verified, and no error. Every retained user-data file in that run kept the same before/after hash. The pre-install backup is under `%LOCALAPPDATA%\ClickyLocal\Backups\<timestamped-backup>`; the private audit retains its exact generated name.

The installed executable reports file version 0.2.0.0, and its product version embeds the exact published source commit. The upgrade report records a successful launch; the exact source revision and transient process identifiers remain in the private final audit rather than packaged documentation.

The installed UI was then visually checked. It displayed version 0.2.0 and the local model reached Ready. The full color palette opened with custom Red, Green and Blue fields. It was canceled, and the stored companion color remained `#386BFF`. The shortcut field physically recorded F8, then the change was discarded without Save; the original shortcuts remain in settings. These observations prove that the installed controls receive input and preserve settings when canceled. They do not prove persistence of a new arbitrary RGB value or a changed shortcut.

Two installed **Auto** requests were exercised against existing windows. Exact **Open Calculator** and **Open Telegram** requests each completed in about 2.7 seconds with one verified action and brought the matching window forward. The Telegram check performed only window activation; no private Telegram content was read. These results cover exact-name opening/activation on this machine, not arbitrary application control.

The remaining acceptance work is explicit: a real human microphone sample; global hold/double-tap shortcut behavior; persistence of a deliberately changed arbitrary RGB value if that behavior needs acceptance; mixed-DPI interaction; authenticated account connectors; and real Office application control. No Office applications were registered in the catalog snapshot. The Notepad executor outcome is verified below, but the independent post-run visual reread was stopped when user input was detected. This evidence does not establish full Clicky parity or production readiness.

## First real app-opening checks

The built refinement opened Notepad and Calculator through actual Auto requests. These are app-opening observations, not proof that a longer task inside either application was completed. Calculator exposed an additional verification defect: its window was visibly open, but `desktop_launch` waited eight seconds and returned only process evidence (`processId: 18824`, `windowVerified: false`).

A read-only native query confirmed the cause. Calculator's top-level handle `4524930` belonged to `ApplicationFrameHost` PID `1820`, which had no package application identity. Its real visible child handle `2491298` belonged to `CalculatorApp` PID `18824`, with the exact AUMID `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`. Matching only the top-level PID could not associate that frame with Calculator.

The source now validates the system frame-host executable and its unique visible child process, including that child's package identity. Window records keep the top-level handle for foreground/UIA operations and expose the verified content process separately from the host. Cached IDs pin host/content start times and the child handle; activation and input revalidate the identity so changed content cannot reuse an old target. Launch/activation messages name the app plainly while structured results retain verification details.

Seventeen read-only checks first passed against the actual open Calculator, including package matching, host/child relationships, stable IDs and rejection of changed process lifetimes, child handles, AUMIDs and cached targets. The promoted harness subsequently passed 8 checks with its optional Calculator group skipped, then 26 checks with the already-open Calculator and no skips. All 18 desktop-smoke checks also passed. No launch, activation, typing, microphone or model request occurred in these identity/catalog harnesses. Evidence: `artifacts/hosted-window-checks/results.json`, `artifacts/hosted-window-smoke/default/results.json`, `artifacts/hosted-window-smoke/calculator/results.json`, and `artifacts/desktop-smoke/results.json`. Later installed checks separately confirmed existing-window activation for Calculator and Telegram and the guarded Notepad typing flow; broader control inspection remains pending.

The reusable harness is now at [scripts/hosted-window-smoke](../scripts/hosted-window-smoke/README.md). It adds ordinary-app, wrong-package/frame-host and stale-cache checks, with optional read-only Calculator coverage. A missing Calculator window is explicitly skipped; the harness never opens it to manufacture coverage.

## Typing fixture: model request failed before input

The next typing fixture encountered an HTTP 500 from the local Qwen worker before any desktop action ran. A separate reproduction identified the request-template problem: context compaction inserted a new system notice before the original system message, producing initial roles `system, system`. The installed model's template rejected the later system message with `System message must be at the beginning.` This failure does not establish that native text insertion is broken or working; the typing flow never reached it.

Before-fix evidence is `artifacts/context-template/before.json`, using the real installed local worker. No actions were executed and the test-owned worker was stopped. The correction merges the compaction notice into the existing leading system message.

The identical actual-Qwen reproduction now succeeds. `artifacts/context-template/after.json` records one leading system message followed by the preserved conversation, 4,698 estimated tokens, and a two-character model reply. The response contained no tool call, so zero desktop actions ran. Raw requests were not written, and the test-owned worker stopped. The final suites pass Core 161/161 and Runtime 51/51 after the correction. This reproduction is evidence only for the context fix; the separate installed typing run below supplies the action evidence.

## Installed Notepad typing verification

The final installed Auto task targeted `heybuddy-typing-check.txt - Notepad` with the exact marker `[HEYBUDDY-LIVE-VERIFY-20260903]`. It first called `desktop_windows` and obtained a fresh `desktop_snapshot`. HeyBuddy then displayed `Approve this action · HeyBuddy` for `desktop_type`, including the exact target and marker, before any typing occurred.

The first `desktop_type` attempt failed closed because the selected control did not expose verifiable editable text. No unverified fallback was treated as success. Auto refreshed the snapshot and retried once within the configured bound. The second result recorded `Success=true`, `performed=true`, `targetVerified=true`, `outcomeVerified=true`, and `foregroundChanged=false`. The run completed after five actions total.

An independent post-run visual state read was started but stopped when user input was detected. It is not counted as separate visual confirmation. The executor's verified outcome is the evidence for the text result.
