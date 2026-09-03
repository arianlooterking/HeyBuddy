# HeyBuddy local AI and document runtime

The Windows UI owns `ModelProviderFactory(settings, credentials)`. `Create()` returns the selected `IModelProvider`; `ModelManager` handles explicit installation, progress, status, startup, shutdown, and model removal. The UI must dispose the factory on shutdown and cancel its current request on emergency stop. Internal `Clicky` namespaces and existing data/model paths are preserved for compatibility.

The factory reuses up to four provider configurations, keyed by provider type, normalized endpoint, model and credential fingerprint. Idle evicted transports are disposed; an active task retains its original configuration and releases its transport when finished. Factory disposal cancels and drains active requests, including evicted providers. Local HTTP connections are reused until the app-managed worker's endpoint/authentication changes. Realtime sessions remain request scoped. `ModelManager.StatusChanged` reports verification/loading/ready/stopped stages, and `LastStartupTiming` separates verification from worker loading.

## Pinned installation and privacy

`ModelManager.Catalog` is the authoritative download list. Qwen model revision is `e87f176479d0855a907a41277aca2f8ee7a09523`, from [Unsloth's Qwen3.5 4B GGUF repository](https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/tree/e87f176479d0855a907a41277aca2f8ee7a09523). The selected files are:

| File | Bytes | SHA-256 |
|---|---:|---|
| Qwen3.5-4B-Q4_K_M.gguf | 2,740,937,888 | `00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4` |
| mmproj-F16.gguf | 672,423,616 | `cd88edcf8d031894960bb0c9c5b9b7e1fea6ebee02b9f7ce925a00d12891f864` |
| llama-b10621-bin-win-cuda-12.4-x64.zip | 250,464,283 | `81c2ff62e14b549cd5c766ccdd5c61f09e821a171655c3047bdccfddc2d1a1e2` |
| cudart-llama-bin-win-cuda-12.4-x64.zip | 391,443,627 | `8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6` |

The CUDA packages come from official [llama.cpp b10621](https://github.com/ggml-org/llama.cpp/releases/tag/b10621), the nightly build referenced by official v0.3.0. Total download: 4,055,269,414 bytes. Weight files use the configured `<model-folder>`; runtime and download archives use `%LOCALAPPDATA%\ClickyLocal\Runtime`. Existing LM Studio models, credentials, processes, and endpoints are not changed.

Downloads resume into `.part` files. Pinned length and SHA-256 must match before promotion; bad files are quarantined with `.rejected-<timestamp>`. The app never launches a partially downloaded binary. Installed runtime binaries have a verification manifest and are hashed before startup. Model hashes are checked at startup and cached only while length and write timestamp remain unchanged within that manager instance.

The app launches a hidden worker on a free `127.0.0.1` port with a fresh random 256-bit bearer token in the child environment. The token is never a command-line argument or saved credential; diagnostic text is redacted. A Windows job object terminates the worker when its owner exits. Stop operates on this worker only. After installation, local inference needs no internet. Cloud fallback does not exist.

Default and effective maximum GPU offload is 24 layers, with 8,192 context tokens, six CPU threads, one inference slot, and the vision projector on CPU. This deliberately leaves desktop GPU headroom on the 8 GB GPU. CPU-only operation is available with zero GPU layers. Context is bounded to 2,048–16,384 tokens; larger context consumes additional memory. A failed worker reports an actionable error instead of claiming a successful model load.

## Providers and credentials

| Provider ID | Configuration | Credential store entry |
|---|---|---|
| `local` | Managed pinned Qwen | Ephemeral worker token only |
| `lmstudio`, `compatible`, `openai-compatible` | `Endpoint` and `Model` | `provider.compatible`, if server requires one |
| `openai` | Explicit `CloudModel` | `provider.openai` |
| `anthropic` | Explicit `AnthropicModel` | `provider.anthropic` |
| `openai-realtime` | `CloudModel`, or documented `gpt-realtime` default | `provider.openai` |

Credentials are supplied through the app's `ICredentialStore` (Windows DPAPI implementation belongs to the desktop). Remote endpoints require HTTPS; HTTP is accepted only for literal loopback addresses or localhost. Embedded credentials, URL query strings, and fragments are rejected. OpenAI and Anthropic use fixed official endpoints. HTTP redirects cannot forward bearer credentials to another endpoint.

Providers stream text, preserve tool calls and result history, and propagate cancellation. Incomplete streams are errors rather than successful replies. HTTP authentication, model, quota, and service failures are presented without echoing provider response bodies or switching to another service. Dotted/long internal tool names receive deterministic valid wire names; replies map back only to registered request/history names. JSON tool arguments remain unchanged.

Cloud images, document-marked messages, tool results, and agent tools require `CloudContentAllowed`. The desktop must wrap imported text in `<document ...>` when composing a user message. Plain typed chat may use an explicitly selected cloud provider without granting screen/file access.

Realtime uses the official [WebSocket GA protocol](https://developers.openai.com/api/docs/guides/realtime-websocket) and [conversation events](https://developers.openai.com/api/docs/guides/realtime-conversations). Each request creates a bounded session, sends real prior messages/tool results plus current text/images, receives transcript deltas and `response.output_audio.delta`, and returns 24 kHz, mono, signed 16-bit PCM in `ModelReply.AudioBase64`. `SpeakReplies=false` requests text only. Supported named voices are validated; otherwise `marin` is selected. Cancellation sends `response.cancel` when possible, then closes/aborts the socket. Audio is not written to disk. This is local microphone transcription followed by optional Realtime generation, not a persistent full-duplex microphone session. No cloud account or paid call was used during development; cloud transport/protocol behavior is covered by deterministic tests and still needs a user's authenticated live validation.

## Files, documents, and sourced reading

`DocumentTools(settings)` is an `IToolExecutor`. Agent file access is confined to `WorkDirectory`, rejects traversal, alternate streams, and reparse points, and cannot import arbitrary external files. Only the desktop's explicit file picker/drop gesture calls `ImportAsync(path, ct)`. It returns `DocumentContent(Name, Path, Text, Truncated, Source, Sha256)` after copying into the workspace's `Imported` folder. Extraction is limited to 50 MB files and 250,000 characters; tools return bounded 24,000-character excerpts with offsets for continuing.

- Text and PDF extraction are implemented. Scanned PDFs report that OCR is required; image input can be used for individual pages. Encrypted or damaged PDFs produce errors.
- DOCX, XLSX, and PPTX extraction is implemented using bounded, DTD-disabled OpenXML parsing. Old `.doc`, `.xls`, and `.ppt` binary formats must be saved in modern Office formats first.
- New text/Markdown/CSV/JSON, DOCX, XLSX, PPTX, and PDF generation is implemented. Existing files are never overwritten. XLSX uses literal string cells, so generated text does not become a formula. DOCX and PPTX mark Persian paragraphs RTL and right aligned.
- English/Turkish PDFs use PDFsharp with local Arial. Persian/Arabic PDFs use installed Microsoft Edge's local print engine for complex-script shaping, with a new temporary profile, escaped text, restrictive no-resource CSP, no user browser profile, and cancellation/timeout cleanup. Without Edge, RTL PDF generation clearly asks for Edge or DOCX/PPTX output.
- Generated RTL PDFs retain logical source text in custom metadata tied to a hash of visible PDF text. This preserves correct re-import of shaped Persian without returning stale source after visible edits. Other PDFs remain subject to their embedded reading order.
- `web.read_url` reads actual public HTTPS text/HTML and returns the final URL, title, retrieval timestamp, and truncation status. It is explicitly a URL reader, not an invented search engine. DNS addresses are checked and pinned for connections; loopback/private networks and nonstandard ports are rejected. Redirects are checked again. Connector search capabilities are separate.

PdfPig 0.1.16 and PDFsharp 6.2.4 are the runtime's justified document dependencies. OpenXML schemas are validated in tests using Microsoft's `DocumentFormat.OpenXml` package, which is a test-only dependency.

## Measured local evidence and checks

Measured on September 3, 2026: NVIDIA RTX 3070 Ti, 8 GB VRAM; approximately 1.8 GB was in use before loading. With 24 GPU layers, CPU projector, 8k context and six threads, the real pinned model returned:

| Check | First token | Total |
|---|---:|---:|
| English short reply, first run | 338 ms | 445 ms |
| Persian short reply, first run | 235 ms | 377 ms |
| Structured `files.list` call, first run | — | 979 ms |
| 640×400 synthetic vision, first run | 2,194 ms | 2,539 ms |
| English short reply, second run | 376 ms | 511 ms |
| Persian short reply, second run | 259 ms | 496 ms |
| Actual workspace tool-result follow-up | — | 601 ms |
| Turkish short reply | — | 398 ms |
| 640×400 synthetic vision, second run | 2,599 ms | 3,391 ms |

Worker startup was 3.8–6.1 seconds. The synthetic image title and both colored shapes were identified correctly. The second run executed the real workspace-list tool against an isolated test folder, then the model correctly named `validation.txt` from its actual result. An unauthenticated inference request returned HTTP 401. The worker stopped after both runs. These are short model/vision measurements, not end-to-end microphone or full-resolution screen latency guarantees.

A later before/after client-lifetime check used five short text requests and five structured tool selections per run with unchanged model/resource settings. Median text latency was 412.9 ms before and 430.4 ms after; tool selection was 1,021.9 ms before and 1,036.1 ms after. These measurements show no generation-speed improvement from connection reuse. The demonstrated improvement was resource retention: 1,000 compatible-provider `Create()` calls retained 1,000 providers before versus one after; allocations decreased from 2,105,176 to 928,984 bytes. No compatible-server request was made for this allocation measurement.

That run's first startup took 26.29 seconds before versus 8.79 seconds afterward, but operating-system file caches and machine load differed, so this is not claimed as a code speedup. The instrumented after run spent 4.07 seconds verifying files and 4.71 seconds loading the worker. An already-running manager returned 1,000 `StartAsync` calls in 3.6–19.6 ms total; repeated startup checks are not a meaningful request bottleneck. Full hash verification and all GPU/CPU limits remain unchanged. Cached-client crash recovery also succeeded with a fresh owned PID/authentication token and preserved SQLite history. Evidence: `scripts/runtime-smoke/output/latency-before.json`, `latency-after.json`, and `recovery-smoke.json`.

A bounded CPU/GPU projector comparison used the actual `artifacts/ui-final/desktop-chat.png` screenshot, prepared by the desktop's `ImagePreparation.ForModel` at a 768-pixel maximum edge. On the same pinned model/runtime, 24 GPU layers, 8k context and six CPU threads, total GPU memory started at 1,251 MiB:

| Projector | First text | Total | Peak total GPU memory |
|---|---:|---:|---:|
| CPU, first image | 3,260 ms | 3,710 ms | 3,740 MiB |
| GPU, first image | 667 ms | 1,160 ms | 4,638 MiB |
| GPU, repeated identical input | 72 ms | 583 ms | 4,638 MiB |

Each answer correctly named HeyBuddy and two visible buttons. The identical-input repeat may benefit from llama.cpp prompt/image caching and must not be reported as a fresh-image result. GPU memory was sampled every 200 ms, with cancellation above 7,168 MiB; the ceiling was never reached. Both benchmark workers stopped and total memory returned to 1,251 MiB. `VisionProjectorGpu` is an optional setting, defaulting to false; this experiment did not write saved settings. GPU vision fit comfortably under this measured desktop workload, but other applications and larger image/context settings can change its memory use. The machine-readable report is `scripts/runtime-smoke/output/vision-projector-benchmark.json`.

The real worker-crash smoke test also passed using the pinned runtime/model, 24 GPU layers, CPU vision, 8k context and six threads. The test verified the loopback listening port's owning PID, exact pinned executable path and parent smoke-process PID, then terminated only that worker during an active streamed response. The interrupted request raised an `IOException` for the closed transport and was not reported as complete. The next request through the same managed provider launched a new owned worker and returned `Local recovery is working.` in 3,567 ms, including restart. A new `AppStore` instance reopened the isolated SQLite database and found the previously committed history entry's ID, content and timestamp unchanged. The test worker stopped afterward; the report and isolated database remain under `scripts/runtime-smoke/output/recovery-smoke.json` and its recorded data directory. This verifies worker-process recovery while the host remains alive; it does not claim a whole-OS power-loss test.

Validation commands:

```powershell
dotnet build src/Clicky.Runtime/Clicky.Runtime.csproj
dotnet test tests/Clicky.Runtime.Tests/Clicky.Runtime.Tests.csproj
dotnet test tests/Clicky.Core.Tests/Clicky.Core.Tests.csproj
dotnet run --project scripts/runtime-smoke/RuntimeSmoke.csproj -- --live --vision scripts/runtime-smoke/vision-test.png
dotnet run --project scripts/runtime-smoke/RuntimeSmoke.csproj -- --documents
dotnet run --project scripts/runtime-smoke/RuntimeSmoke.csproj -- --benchmark-vision artifacts/ui-final/desktop-chat.png
dotnet run --project scripts/runtime-smoke/RuntimeSmoke.csproj -- --recovery-smoke
```

Add `--install` to the smoke command only to perform the explicit verified model/runtime install. The normal desktop provides the same operation in its model controls; no terminal is required for daily use.

Runtime tests cover streaming, tool history, cloud consent, safe endpoints, failure handling, cancellation, resumed downloads, wrong checksums, file confinement, document round-trips, official OpenXML validation, Persian PDF shaping/re-import, and Realtime PCM/session cleanup. Persian PDF output was rasterized and visually inspected for joined glyphs, correct right alignment, and mixed-language layout.

Core tests exercise actual SQLite persistence and literal search, backup restoration, restart recovery, denied/sensitive actions, late model results after cancellation, action recording during cancellation, 30-action limits, two-retry limits, unknown-tool rejection, malformed/dangerous guidance, memory traversal rejection, and backup retention when skills are toggled.

Tool discovery keeps at most 20 definitions and 14,000 schema/description characters in a model request. It starts with local built-ins plus a small relevant connector subset. `tools.search` searches registered metadata and exposes selected real definitions in later requests; it performs no connector operation. Oversized schemas are reported rather than truncated into invalid contracts. Actual execution still resolves against the registered executor and passes through unchanged permission checks.

`ContextBudget.Fit(request, contextTokens)` also sizes the complete request, with a reply/framing reserve, conservative Unicode-aware text estimates, and an image allowance. Agent memory context is explicitly excerpted, and each tool result is limited to 6,000 characters in a valid envelope. The current user instruction, system instructions, current images, and newest complete tool exchange stay intact structurally. Result text may be excerpted with a clear notice; exact call arguments and matching result IDs remain. Older tool exchanges are omitted as whole groups, never orphaned results. A mandatory current instruction or tool exchange that cannot fit fails before another model/tool dispatch and asks the user to narrow the request or raise context. This is conservative estimation rather than an exact model tokenizer; exceptionally large images or tokenizer differences may still cause the provider to report a context-limit error.
