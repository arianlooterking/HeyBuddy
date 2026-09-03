# Windows input, capture, and local speech

The native implementation uses Windows UI Automation for inspection and editable values, Win32 input for verified foreground clicks/navigation, DPAPI for per-user credentials, NAudio for devices/PCM, Whisper.net for CPU transcription, and a separately installed Piper process for local speech. It runs unelevated with per-monitor-v2 DPI awareness. No native component starts recording or sends cloud traffic on construction.

The installed build is 0.2.0. The app picker and exact-name launch/activation paths are included; installed Auto checks brought existing Calculator and Telegram windows forward with verified actions. An approval-gated installed Auto task also typed an exact marker into a named Notepad document. Its first control snapshot failed safely, a fresh snapshot enabled the bounded retry, and the executor verified the target, text outcome, and unchanged foreground. A separate visual reread was stopped when user input was detected and is not claimed. The broader cross-application input matrix remains incomplete.

## Public integration surface

`Clicky.Windows.Native`:

- `DpapiCredentialStore : ICredentialStore` provides `Get`, `Set`, and `Delete`. Filenames are hashes of credential names, contents are DPAPI ciphertext scoped to the signed-in Windows user, and writes use temporary-file replacement. The optional constructor directory supports isolated tests.
- `ScreenCaptureService.GetMonitors()` returns physical display bounds, stable Windows device names, and primary status. `CaptureMonitor(id)`, `CaptureRegion(System.Drawing.Rectangle)`, `CaptureWindow(nint)`, and `CaptureForeground()` return `ScreenCapture` with a PNG held in memory and its physical screen origin.
- `WindowsDesktopTools : IToolExecutor` exposes `desktop_apps`, `desktop_launch`, `desktop_windows`, `desktop_activate`, `desktop_snapshot`, `desktop_click`, `desktop_type`, `desktop_key`, and `desktop_scroll`. `ListAppsAsync(query, cancellationToken)` returns typed `DesktopApp` choices; `ActivateWindowAsync(windowId, cancellationToken)` restores and verifies a listed window.
- `HotkeyManager(AppSettings)` exposes `ActionInvoked(ShortcutAction, HotkeyGesture)` and `PointerClicked(System.Drawing.Point)`. Call `Start()` on the UI thread with a running message pump and dispose when reconfiguring shortcuts or exiting. Events are posted to the captured synchronization context. Press/release and 400 ms double-taps are distinct. Injected input does not trigger global shortcuts or walkthrough progression.
- `DictationInserter.InsertAsync(text, expectedWindow, cancellationToken)` pastes to an explicitly remembered foreground window. It snapshots clipboard formats, waits briefly for shortcut modifiers to be released, refuses a changed foreground/clipboard, and restores the prior clipboard only if the clipboard sequence still belongs to its insertion.

`Clicky.Windows.Speech`:

- `SpeechService(AppSettings)` reads current settings for microphone/output IDs, voice, language, speed, CPU threads, and the correction dictionary.
- `InstallAsync(Action<string>?, cancellationToken)` and `InstallAsync(IProgress<string>, cancellationToken)` install/verify speech assets. `IsInstalled`, `Assets.RecognitionInstalled`, and `Assets.VoicesInstalled` expose actual local file availability. The installation path reports SHA-256 verification, whereas the cheap availability properties only check file existence.
- `GetMicrophones()` and `GetOutputDevices()` return `AudioDevice(Id, Name)`. ID `-1` means the Windows default; disconnected explicit devices generate an actionable error.
- `StartRecording(onPartial)` starts explicit 16 kHz mono PCM capture and freezes the selected input device and recognition language for that recording. Every 3.5 seconds, a preview can re-decode accumulated audio. `StopAndTranscribeAsync(onPartial, cancellationToken)` stops capture and streams final transcription segments. Recording is capped at 120 seconds; PCM stays in memory.
- `LastCaptureStatus` and `CaptureStatusChanged` expose audio metadata and actionable messages: actual device, duration, RMS/peak, gain, requested/detected language, and recording/recognition outcome. No PCM or transcript is retained in this diagnostic record. No frames, too-short clips, silent input, quiet signal, empty recognition, cancellation and errors have separate states. Quiet valid input receives gain capped at 32; exact silence, DC-only input and quantization jitter are rejected before Whisper. Low volume alone no longer prevents final transcription.
- `CaptureUtteranceAsync(onPartial, cancellationToken)` is a single utterance operation for explicitly enabled hands-free mode. A low energy threshold supplies an activity hint, ends the utterance after 1.1 seconds without activity, and caps capture at 30 seconds. It is not a final transcription gate or proof of human speech; background noise can keep this bounded capture active. It never enables its own recurring listening loop. `VoiceActivity`, `AudioLevel` and capture-status events may run off the UI thread, so UI handlers must dispatch appropriately.
- `SpeakAsync(text, language, cancellationToken)` synthesizes and plays local Piper audio. Voice speed maps to Piper length scale; supported speed is 0.5–2.0. Explicit `en`, `fa`, or `tr` is preferred; automatic language selection uses script/character heuristics.
- `PlayPcmAsync(byte[], sampleRate, cancellationToken)` plays bounded 16-bit mono PCM through the selected output and participates in the same stop controls. It supports an explicitly selected cloud provider returning audio without changing local transcription.
- `StopPlayback()` interrupts playback/synthesis. `Stop()` additionally cancels active recording and transcription. `Dispose()` cancels work and releases Whisper after native decoding has exited.
- `SynthesizeAsync()` returns PCM/sample-rate/language for tests or export features; `TranscribeWavAsync()` resamples a WAV stream and decodes it locally. `Measured` emits timings without audio contents.

`Clicky.Windows.Views.SketchWindow(ScreenCapture)` presents an ephemeral annotated-capture dialog with draw/erase, color, width, clear, bounded undo/Ctrl+Z, and Use/Cancel controls. On a successful `ShowDialog()`, its nullable `Result` contains the PNG composition at the original physical pixel dimensions and screen origin. The preview's Viewbox changes only display scale; it does not rescale exported screenshots or drawing coordinates.

## Input invariants

The app catalog reads registered Windows AppsFolder entries, Start menu shortcuts without arguments, and user/machine App Paths registrations. Packaged applications use their registered AUMID activation contract. Search scans the registrations and returns at most 100 results; the user picker shows at most 60 and asks for a narrower query when needed. Duplicate names remain separate entries. Only an issued app ID whose current registration/file identity still matches can launch. Free-form executable paths, shell commands, scripts, arguments and URLs are not accepted. Console programs, command interpreters and installer/uninstaller entries are excluded.

Auto's complete, single-app opening requests in English, Persian or Turkish and the **Apps & actions → Open** button call `desktop_launch` through the same common permission layer. They do not require a model request. Longer or compound requests use the agent flow. Missing and ambiguous apps return a choice/error instead of guessing. One matching existing window is reused; several existing windows require selecting a listed window. `desktop_activate` may restore a minimized window but must verify its final foreground identity; Windows can refuse focus.

Window IDs combine the native handle, process ID, and process start time. Actions cannot fabricate a raw window handle. Accessibility snapshots contain cached controls with runtime IDs and expire after 90 seconds; only the latest snapshot ID for a window is accepted. Hidden, disabled, password, protected, and elevated targets are refused.

Every input requires the target to be foreground immediately before the operation. Clicks require a UIA clickable point and confirm that the actual window beneath that point is the expected window. Text entry first uses an editable UIA ValuePattern and compares the resulting value. The refinement adds visible Unicode input for an explicitly editable TextPattern Edit/Document control, with exact element focus, held-modifier checks, and unchanged foreground/cancellation checks between batches of at most 32 UTF-16 code units. This fallback leaves the clipboard untouched and verifies the resulting text. Scroll requires a ScrollPattern; unsupported controls receive an error. Keyboard input is limited to navigation/editing keys and selected simple chords.

App launch and window activation are `RiskLevel.LocalWrite`; app/window listing and snapshots are read-only. Click, type, key and scroll remain `RiskLevel.Sensitive`, so the common agent approval layer must show a concrete action before input that could submit a form or change external data. A visual arrow, drawing, or walkthrough step never calls these tools. The executor itself is not an independent approval UI; host applications must not bypass the common approval gate.

Tool results distinguish `performed`, target verification, and outcome verification. A click/key being delivered does not prove a business operation succeeded. A result with `performed: true` or `completionUnknown: true` must never be retried automatically, even if `Success` is false. UIA work has a cancellation budget and caller wait limit; cancellation prevents subsequent input after an unresponsive provider returns.

A successful launch can establish that a process is running even if no controllable window was found; the result reports that distinction. It does not establish sign-in, accessibility support or completion of work inside that app. Protected, elevated and locked desktop targets remain refused. Office document tools are built in, but operating the native Office applications requires genuine installed registrations; similarly named utilities are not substitutes.

`PrintWindow(PW_RENDERFULLCONTENT)` captures the selected window, avoiding unrelated windows covering its screen rectangle. Failure or an all-black sampled result is reported instead of falling back silently to a desktop crop. Some GPU/protected surfaces are unsupported; an explicitly selected visible region is the fallback the user can choose. PrintWindow is a synchronous OS call; the host should invoke capture off the UI thread when it needs cancellation or a responsiveness timeout.

## Installed speech artifacts

Speech models use the configured `<model-folder>\Speech`; the runtime defaults to `%LOCALAPPDATA%\ClickyLocal\Runtime`. Existing models are not moved. Failed/interrupted downloads remain `.part` files for resumption. Verified completed files are promoted atomically. Runtime/model downloads use these pinned sources:

| Artifact | Pin |
| --- | --- |
| Whisper multilingual small | `ggerganov/whisper.cpp` revision `5359861c739e955e79d9a303bcbc70fb988958b1`; `ggml-small.bin`; SHA-256 `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b` |
| Piper Windows x64 | `rhasspy/piper` release `2023.11.14-2`; archive SHA-256 `f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea` |
| English | `en_US-lessac-medium` |
| Persian | `fa_IR-amir-medium` |
| Turkish | `tr_TR-dfki-medium` |
| Voice repository | `rhasspy/piper-voices` revision `39ab474be869e9181350af6a65e4953eef67aaa0`; ONNX/config SHA-256 values are pinned in `SpeechAssets` |

The current maintained Piper repository changed licensing and packaging; this application uses the original standalone Windows release as an isolated local subprocess. Runtime libraries and voice models carry their own licenses. Installation preserves each voice's `MODEL_CARD` next to the downloaded model, and the Piper runtime archive contains its bundled notices. See the [original Piper release](https://github.com/rhasspy/piper/releases/tag/2023.11.14-2), [Piper voice model repository](https://huggingface.co/rhasspy/piper-voices), [Whisper.net documentation](https://github.com/sandrohanea/whisper.net), and [Whisper model repository](https://huggingface.co/ggerganov/whisper.cpp).

## Verification and remaining limits

Run the owned-window integration harness:

```powershell
dotnet run --project tests/Clicky.Native.Tests -- --speech
dotnet run --project tests/Clicky.Native.Tests -- --speech-diagnostics
dotnet run --project tests/Clicky.Native.Tests -- --render
```

The harness creates its own visible fixture windows and performs input only against them. It validates UIA inspection, private window capture, secondary-monitor physical coordinates, password refusal, cached-window/snapshot refusal, verified text entry and clicking, cancelled input, foreground-change refusal, Persian clipboard insertion/restoration, DPAPI, shortcut hook lifecycle, passive microphone initialization, selected-device PCM playback, cancellation, SHA verification, and actual local synthesis/transcription. Artifacts are in `artifacts/native/`; screenshots contain only the test fixtures. Real speech assertions establish functioning synthesis/recognition, not exact transcription quality.

The separate `--render` check passes without manipulating desktop focus. It verifies actual annotation rasterization at 1200×800, preservation of a negative monitor origin, the stroke's expected pixel coordinates, and preservation of unmarked source pixels. `sketch-composition.png` was visually inspected; `sketch-result.json` records the assertions.

The separate `--speech-diagnostics` mode passes 25 checks using audio generated in memory, without opening a microphone. Automatic and explicit EN/FA/TR decoding return words in the expected language; wording is imperfect. A quiet English sample at RMS 0.000626, below the old 0.012 gate throughout, now returns the full expected sentence with bounded gain. Silence/DC/one-step jitter do not invoke Whisper, and cancellation permits no later transcript callback. Evidence: `artifacts/native/speech-diagnostics.json`.

For the 0.2.0 source refinement, `scripts/desktop-smoke` passes 18 non-interactive checks covering discovery, registration validation, risk declarations, invalid IDs, cancellation and Unicode packets. Targeted discovery took 1.6–2.2 seconds on the test PC. No genuine Word, Excel or PowerPoint registrations were available in that snapshot. This harness did not launch an application, change focus or type. See [the independent harness](../scripts/desktop-smoke/README.md) and `artifacts/desktop-smoke/results.json`. Later installed checks separately verified Calculator/Telegram activation and the guarded Notepad typing path described above.

An initial run passed all 13 native checks (including actual click, text entry, focus refusal, and clipboard restoration). Extended runs additionally passed capture on the second display (including negative Y origin), selected-device PCM playback/cancellation, and EN/FA/TR speech. Earlier foreground checks encountered a protected `GameInputSvc` foreground window and correctly refused to type/click; that diagnosis is historical. The latest `results.json` is authoritative for its recorded run; an interrupted desktop session must not be reported as a clean combined pass.

### Read-only foreground check: 2026-09-03 10:14:52 UTC

That historical read-only check observed `LockApp` PID 35976 as foreground, with window handle `0x2C0B5C`, in session 1. The test shell was also in session 1; both thread desktop and input desktop reported `Default`. `GameInputSvc` remained present in sessions 0 and 1 but did not own foreground. No HeyBuddy or native fixture processes were running in that snapshot.

The lock screen blocked that run. No attempt was made to unlock it, stop a service, bypass target/elevation checks, or access an existing owner document. The normal 0.1.1 app was subsequently brought forward and its Settings navigation/scrolling were checked; the snapshot is not a claim about the current desktop state. Extended cross-app input/dictation acceptance remains incomplete. Evidence: `artifacts/native/foreground-status.json` and the later installed checks in [validation.md](validation.md). This observation is not a human microphone test and does not change the historical synthetic speech measurements below.

On this PC, an initial multilingual-small round trip measured:

| Language | Piper synthesis | Whisper transcription | Generated audio duration |
| --- | ---: | ---: | ---: |
| English | 596 ms | 2,548 ms | 3.31 s |
| Persian | 804 ms | 2,355 ms | 4.06 s |
| Turkish | 596 ms | 2,236 ms | 4.41 s |

These are synthetic local voice → local transcription samples, not microphone benchmarks or full assistant-response latency. English and Turkish were substantially recognizable; names/punctuation varied. Persian showed meaningful word/boundary errors despite explicit Persian decoding. Multilingual small was selected over base after base produced substantially worse Persian/Turkish output. Users should review dictation before sensitive actions; no flawless multilingual accuracy claim is justified.

A further same-audio Persian comparison used explicit `fa` decoding and a Persian prompt: small took 2,449 ms and medium-q5_0 took 6,768 ms. Medium did not materially resolve the wording errors, so the default remains small. The SHA-verified medium benchmark file is preserved in the model directory but is not loaded by default or required by installation.

Human microphone intelligibility, echo/barge-in performance, physical keyboard hold/double-tap gestures, restart while a device disappears, elevated-window interaction refusal in a controlled elevated fixture, and dictation in Notepad/Chromium/VS Code/Office require separate interactive validation. The current VAD is an adjustable-in-code energy threshold, not speaker isolation or echo cancellation. Model runtime failures are surfaced; no cloud provider is selected automatically.
