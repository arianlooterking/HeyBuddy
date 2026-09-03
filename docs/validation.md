# HeyBuddy validation record

Date: September 3, 2026. Target: Windows 11 Pro build 28120, i7-12700K, approximately 32 GB RAM, RTX 3070 Ti 8 GB. Two monitors were detected: 3840×2160 primary and 1920×1080 secondary with a negative vertical origin.

**This is a tested local build, not a completed full-parity release.** Account-dependent work is separate from the remaining local interactive acceptance checks below. Test fixtures are never presented as authenticated owner accounts.

## 0.2.0 release and installed validation

Release 0.2.0 was built and installed over the previous copy. The installed executable reports file version 0.2.0.0. Package, upgrade and selected live UI checks are complete; broader interactive acceptance remains open. The earlier records below retain their original scope and should not be read as fresh full-suite evidence for the installed update.

The source now routes default **Auto** conversation through the common tool runner. **Agent** remains a background task and cannot report completion from prose or tool discovery alone: it requires an actual successful tool action. **Chat only** explicitly runs without tools, and **Dictate** keeps its existing text-insertion workflow. **Apps & actions** lists installed apps; complete single-app opening requests in English, Persian and Turkish can use the same permission layer without a model call. Ambiguous names require a choice. **Load local AI when HeyBuddy starts** defaults on for installed local models and exposes verification/loading/ready stages, with cancellation through Stop everything. GPU layers, context and CPU limits have not changed.

Current bounded evidence:

| Check | Observed result and limits |
|---|---|
| Final build | Release build completed with zero errors and zero warnings; aggregate automated failures were zero. |
| Core | 161/161 tests passed. |
| Runtime/provider suite | The latest Runtime suite passes 51/51, including keyed client reuse, eviction, active-request cancellation and changing cloud-content permission. No account/cloud calls were made. |
| Connectors | 12/12 protocol and policy tests passed; 10/10 connector UI checks remain unauthenticated fixtures. |
| Conversation routing | 41/41 checks passed, including the rule that a requested state change cannot complete after read-only inspection alone. |
| Shortcut and color controls | 39/39 fixture checks passed. The installed shortcut field also physically captured and canceled an F8 edit without changing the saved shortcuts. |
| Client retention | 1,000 identical compatible-provider selections retained one provider instead of 1,000; measured allocation fell from 2,105,176 to 928,984 bytes. This check made no network request. |
| Warm local text / tool selection | Five-sample medians: 412.9 → 430.4 ms for short text, 1,021.9 → 1,036.1 ms for tool selection. These results do not show faster model generation. |
| Cold local startup | Separate runs measured 26.29 and 8.79 seconds; file cache and machine load differ, so the change cannot be attributed to client reuse. The latter run spent 4.07 seconds verifying and 4.71 seconds loading. Preload moves that wait earlier; it does not remove the work. |
| Crash after client reuse | The test-owned authenticated worker was terminated during streaming. The next local request used a new worker and completed in 5.496 seconds; a prior SQLite history record was preserved. Both test workers stopped afterward. |
| Native app discovery | 18 non-interactive checks pass: registered-app discovery, allowed launch identities, risk declarations, invalid IDs, cancellation and Unicode packets. Targeted discovery measured 1.6–2.2 seconds; no app was launched or typed into by this harness. |

Evidence: `scripts/runtime-smoke/output/latency-before.json`, `latency-after.json`, `recovery-smoke.json`, runtime test sources/results, and `artifacts/desktop-smoke/results.json`. The inference checks used the existing pinned model with 24 GPU layers, 8,192 context tokens, six CPU threads and CPU vision. Existing model servers, user settings and owner data were preserved.

### Package and upgrade evidence

| Artifact or check | Observed result |
|---|---|
| Installer | `HeyBuddy-0.2.0-Setup-x64.exe`; exact outer hash is supplied in the GitHub Release `SHA256SUMS.txt` asset. |
| Portable ZIP | `HeyBuddy-0.2.0-win-x64.zip`; exact outer hash is supplied in the GitHub Release `SHA256SUMS.txt` asset. |
| Payload validation | The final validation covered the complete SHA-256-manifested payload. The upgrade verified every installed payload file. |
| Existing data | All five retained user-data files in the final upgrade kept identical before/after hashes. |
| Backup | `%LOCALAPPDATA%\ClickyLocal\Backups\<timestamped-backup>`. The private audit retains the exact generated folder name. |
| Upgrade and launch record | `artifacts/release/upgrade-0.2.0.json` records `Success: true`, no error and a successful launch. Transient process identifiers are omitted here. |
| Installed binary | File version 0.2.0.0; product version embeds the exact published source commit. |

Exact outer-package hashes are supplied beside the installer and ZIP in the GitHub Release `SHA256SUMS.txt` asset. They are not duplicated inside packaged documentation because changing that documentation changes the package hashes. The self-contained package is unsigned and does not contain model downloads.

### Post-install interactive observations

The installed UI visibly showed version 0.2.0 and the local model reached Ready. The full palette, including custom Red, Green and Blue fields, opened successfully. Canceling it preserved `#386BFF`. The installed shortcut field physically captured F8; that edit was deliberately discarded without Save, and the original shortcut values remain. These checks demonstrate interaction and cancellation behavior. They do not establish persistence for a new arbitrary color or shortcut.

Installed **Auto** requests for exact **Open Calculator** and **Open Telegram** each completed in about 2.7 seconds with one verified action and brought the existing matching window forward. The Telegram check did not read private content. This proves two exact-name activation paths, not general control of either application.

The mode routing and Apps & actions picker have bounded fixture evidence. The installed UI adds direct evidence for the preload status reaching Ready and for exact-name window activation. The final installed Auto typing task showed a `desktop_type` approval preview for the exact target `heybuddy-typing-check.txt - Notepad` and marker `[HEYBUDDY-LIVE-VERIFY-20260903]`. Its first attempt failed closed on unverifiable editable text; after a fresh snapshot, one bounded retry succeeded. The five-action run ended with `performed=true`, `targetVerified=true`, `outcomeVerified=true`, and `foregroundChanged=false`. An independent visual reread was stopped when user input was detected and is not claimed as separate evidence.

A human microphone sample remains pending. No genuine Word/Excel/PowerPoint registrations were available, so there is no Office interaction claim. Authenticated connector accounts, global hold/double-tap shortcuts, mixed-DPI interaction, and the other limits below remain unchanged. Follow the separate [refinement record](refinement-2026-09-03.md) for the detailed sequence. This remains a partial local validation, not full parity or production-readiness certification.

## Automated and real local checks

This table records the earlier local validation build. It is not a fresh whole-solution result for the 0.2.0 source changes.

| Check | Result / evidence |
|---|---|
| Whole solution Release build | Pass, zero errors and warnings |
| Core tests | 43 passed: SQLite, backup/restart, approvals, cancellation, retry/action limits, guidance isolation, context/tool discovery, memory paths |
| Runtime tests | 39 passed: streaming/providers, downloads/hash/resume, local network policy, document formats and RTL PDF, Realtime fixtures |
| Connector tests | 12 passed: actual local HTTP/stdio protocol exchange, OAuth state/refresh fixtures, credentials, tool controls, path boundaries |
| Formatting verification | `dotnet format Clicky.slnx --verify-no-changes` passes |
| NuGet vulnerability report | No vulnerable packages reported by NuGet, including transitives; `artifacts/dependency-audit.json` |
| Connector native UI | 10 checks pass; desktop/compact screenshots visually inspected, secure environment fields and tool toggles tested |
| Main native UI | All seven pages render at 1120×780 and 780×620; final captures inspected for layout; real Persian response displays RTL and persists |
| Local agent | Real Qwen produced `files.write_text` then `files.read`, created requested text and verified content in isolated workspace; two actions |
| Sketch | Original-resolution composition, preserved source pixels, stroke location and negative monitor origin pass |
| Image preparation | Seven raster checks pass: size limits, aspect ratio, unchanged small images, corrupt/oversized input rejection |
| Model worker | Pinned model/projector/runtime installed; actual text, vision and tools; unauthenticated loopback receives 401; worker exits after tests |
| Actual worker crash/recovery | Test-owned PID terminated during streaming; interrupted request failed, next local request restarted the worker in 3.567 seconds; prior SQLite history preserved |
| Speech | Actual local Piper → Whisper EN/FA/TR samples; device enumeration and selected-output PCM cancellation; no real microphone recording retained |

Machine-readable evidence: `artifacts/test-results/*.trx`, `artifacts/optimized-acceptance/ui-result.json` (all 18 checks pass), `artifacts/connector-ui/result.json`, `artifacts/native/sketch-result.json`, and `artifacts/native/image-preparation-results.json`. The final installed package checks are appended below after packaging.

## Measured latency

These are actual local measurements, not universal performance guarantees. Generated audio samples exclude human speaking duration and the hands-free silence detector's 1.1-second endpoint delay.

| Measurement | Observed |
|---|---:|
| Warm English recognition → local model → reply PCM ready | 3.87–4.45 seconds; installed copy 4.22 seconds |
| Recognition component | 2.04–2.30 seconds |
| Model component | 0.62–0.71 seconds |
| Reply synthesis component | 1.15–1.66 seconds |
| Actual native app window, 768-pixel preparation and CPU vision | 4.00–4.08 seconds |
| Same 768-pixel app screenshot in controlled CPU / GPU comparison | 3.71 / 1.16 seconds |
| Peak total GPU memory during CPU / GPU vision comparison | 3,740 / 4,638 MiB, from 1,251 MiB baseline |
| 640×400 synthetic screen analysis | 2.54–3.39 seconds total |
| Short text first token, direct model smoke | 0.235–0.376 seconds |
| Initial UI conversation, including verification/load and full request | 6.6–58.9 seconds across runs; installed copy 6.61 seconds |

The slower initial conversation includes file verification and loading. A warm short model request should not be confused with first-use voice latency. A full-resolution native capture initially took 68.38 seconds; the app now prepares images to a configurable 768-pixel maximum edge by default, keeping original screen coordinates for drawings. The final native capture took 4.08 seconds and correctly identified HeyBuddy and visible window controls. The controlled GPU comparison used the same prepared screenshot and is detailed in `docs/runtime.md`. GPU image processing is an explicit settings option; CPU remains the default. Fine text may require a smaller selected region or higher quality.

## Live connector evidence

`artifacts/connector-live/results.json` records isolated probes without saving user configuration:

- Public web read: passed in 629 ms.
- Maps public read: passed in 840 ms.
- Polymarket: timed out after 20.03 seconds; unavailable in that check.
- Installed Codex MCP initialization/tool listing: passed in 619 ms, two tools.
- Installed Claude MCP initialization/tool listing: passed in 537 ms, three tools.
- Neither CLI tool was invoked; account reads remain unverified. No Google, Spotify, workspace/developer account or cloud AI key was used. No service writes, messages, purchases or publishing occurred.

## Local acceptance still requiring interactive validation

An initial controlled native fixture run passed 13 checks, including actual clicks, text, focus refusal and clipboard restoration. Extended tests passed second-display capture and audio separately. Later focus-dependent runs were refused because a protected `GameInputSvc` window owned the foreground. The app did not bypass the target check or modify that service. `artifacts/native/results.json` records these failures; the combined extended native suite is not a clean pass.

At 10:14:52 UTC, a read-only recheck found **LockApp** owning the foreground in the user's session. No unlock attempt or protected-service change was made. That observation is historical: after the 0.1.1 upgrade, the normal app was brought forward and its Settings navigation and scrolling worked through Computer Use. The extended cross-application acceptance suite remains incomplete. Earlier evidence: `artifacts/native/foreground-status.json`.

Remaining local checks: actual dictation and walkthroughs in Notepad, Chromium and VS Code; Office interaction if genuine Office applications become available; global hold/double-tap keyboard gestures; human EN/FA/TR microphone samples; hands-free echo/interruption behavior; microphone unplug/switch during recording; mixed-DPI interaction; controlled elevated-window fixture; network-disabled desktop operation; deliberate GPU-memory exhaustion; and upgrade/restart recovery with pending tasks. The installed Auto Notepad typing result is executor-verified; only its independent visual reread was stopped by user input. The 0.1.1-to-0.2.0 package upgrade and preservation check passed. Core/protocol simulations cover several related failure paths, but do not replace the remaining checks.

Persian small-model recognition has meaningful word/boundary errors. A medium model comparison was slower and did not materially improve the tested phrase. This remains a quality limitation. OpenAI Realtime currently uses local transcription followed by a request-scoped cloud audio response; it is not an always-connected cloud microphone session. Arbitrary compatible model capabilities are not automatically proven by a model ID.

## Review fixes

### 0.1.1 cursor and microphone correction

The running 0.1.0 Settings page could save an old companion size over a change made outside that process. Cursor size now saves and applies from the actual UI, with synchronized right-click size choices. New profiles default to 50%. Thirteen isolated cursor checks pass: half-size geometry and raster bounds at 96/144/192 DPI, compact idle bounds, persistence, menu state, and unchanged reply text size. Evidence: `artifacts/cursor-smoke/results.json`.

The live recording path previously rejected every clip without an 80ms frame at RMS 0.012 or above, before Whisper could attempt transcription. It now analyzes the captured signal and applies bounded gain to quiet input. The settings page includes an eight-second local microphone test and a live level meter; input and recognition-language selection save immediately. Diagnostic messages distinguish no frames, a short recording, silent input, and a recognizer returning no words. Whisper processors now dispose asynchronously so cancellation waits for native processing to end safely.

Twenty-five speech checks pass. A real Piper English sample attenuated to RMS 0.000626 and peak 0.003998 (below the old threshold throughout) now transcribes the full expected sentence with gain capped at 32. Silence/DC/one-step jitter do not invoke Whisper. EN/FA/TR samples returned words in the expected language with both automatic and explicit recognition; Persian and Turkish wording was imperfect. Automatic language detection also passed before the fix and was not the demonstrated cause. In-flight cancellation produces no later transcript callbacks. Evidence: `artifacts/native/speech-diagnostics.json`. These generated samples verify the recognizer and input processing, not the owner's live microphone.

The solution build and all 94 existing tests passed after the fixes. Formatting verification passed after whitespace corrections. Thirteen isolated Settings/recording-ownership checks pass in the installed executable: size field/menu synchronization, immediate language/microphone persistence, and refusal to interrupt an active transcription or microphone test. The installed executable also passes all 15 page-render/RTL fixture checks. Evidence: `artifacts/installed-hotfix-settings/settings-result.json`, `artifacts/installed-hotfix-ui/ui-result.json`, `artifacts/hotfix-verification.log`, and `artifacts/hotfix-format.log`. The verification log includes the initial formatting failure; the separate final formatting log has exit code 0.

Historically, the 0.1.1 installer returned 0 and all 536 installed payload files matched that package. Its outer hashes were supplied in that release's `SHA256SUMS.txt`. All five existing data files retained their hashes across installation. A backup was created under `%LOCALAPPDATA%\ClickyLocal\Backups\<timestamped-backup>`. Only the intended preference keys changed afterward: companion scale 1.0 → 0.5 and microphone -1 → USB input 0. The normal installed app restarted as version 0.1.1.0 with a responding window. Evidence: `artifacts/release/upgrade-0.1.1.json` and `artifacts/release/launch-0.1.1.json`.

The live app was visually checked: the smaller companion is visible, the selected USB input is displayed, and the eight-second test button is accessible without clipped controls. It was left at that test. Windows reported the USB microphone unmuted at 100% before the update, but no human microphone sample was captured in this repair session. Human voice acceptance remains pending.

The implementation review fixed document/screen context crossing to cloud without consent, mutable dictation destinations, hands-free/manual recording ownership races, action results disappearing on cancellation, repeated failed tool calls exceeding two retries, malformed guidance crashes, skill-toggle backup loss, and tool/context overflow. Sensitive tool checks remain in the common runner, independent of model or connector hints. Returned native effects are recorded before cancellation blocks subsequent actions.

Canvas UI reuse was checked by a direct seven-tool MCP handshake and global skill validation. Its saved global MCP entry becomes available after Codex reload. No web component runtime was inserted into WPF.

## Earlier built and installed 0.1.0 package

Historically, the 0.1.0 release script completed with zero warnings/errors. Its installer, portable ZIP, and `SHA256SUMS.txt` were published as a matched set. All 536 payload files and 537 ZIP entries were verified, with no mismatches. Personal data and model weights were excluded. The build was unsigned.

The per-user installer returned exit code 0 and installed to `%LOCALAPPDATA%\Programs\HeyBuddy`. Both the Start menu and desktop shortcuts point to that installed executable. All 536 installed payload hashes match. The runtime configuration includes .NET and Windows Desktop 10.0.8, so daily use needs no SDK, terminal or manually started inference server.

The **installed executable** passed all 18 `--self-test --live` checks using isolated data, including actual local Persian chat/history, speech recognition/model/synthesis, capture analysis and a two-action document agent. Evidence: `artifacts/installed-acceptance/ui-result.json`. Installed measurements were 4.222 seconds to reply PCM ready and 4.000 seconds for native-window analysis.

A same-version reinstall also returned 0; the four existing isolated data files containing history/tasks, profile and generated content retained identical hashes. This is a reinstall preservation check, not proof of an upgrade across schema versions. See `artifacts/release/reinstall-preservation.json`.

After the tests, the normal installed application launched and reported a responding **HeyBuddy** main window. It is available from the tray, desktop and Start menu. Installation, payload and launch evidence are in `artifacts/release/installation.json`, `install.log`, and `launch.json`. This section was added after packaging; the packaged documentation describes the pre-install validation, while this repository record includes installed verification.
