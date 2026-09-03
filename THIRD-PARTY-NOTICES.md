# HeyBuddy dependency notices

HeyBuddy's new source is independent of the private HeyClicky Mac application. The supplied DMG is not redistributed. The older [farzaa/clicky](https://github.com/farzaa/clicky) MIT project was consulted as a reference; no private backend or Mac executable is included.

Runtime dependencies are pinned in project files and download manifests. Preserve upstream licenses when distributing binaries. This personal build downloads models and external engines separately; they are not included in its installer or portable ZIP.

| Dependency | Version / source | License information |
|---|---|---|
| .NET / WPF / Microsoft.Data.Sqlite.Core | .NET 10, Sqlite package 10.0.0 | Microsoft .NET MIT notices; bundled framework notices retained |
| SQLitePCLRaw / native SQLite | bundle_e_sqlite3 3.0.3 | Apache-2.0 wrapper; SQLite public domain |
| MCP C# SDK | ModelContextProtocol.Core 2.2.0 | MIT, [official SDK](https://github.com/modelcontextprotocol/csharp-sdk) |
| NAudio | 2.3.0 | MIT, [NAudio](https://github.com/naudio/NAudio) |
| Whisper.net / whisper.cpp | 1.9.1 runtime | MIT, [Whisper.net](https://github.com/sandrohanea/whisper.net) |
| PdfPig | 0.1.16 | Apache-2.0, [PdfPig](https://github.com/UglyToad/PdfPig) |
| PDFsharp | 6.2.4 | MIT, [PDFsharp](https://github.com/empira/PDFsharp) |
| llama.cpp | b10621 Windows CUDA | MIT; CUDA libraries retain NVIDIA terms, [release source](https://github.com/ggml-org/llama.cpp) |
| Qwen3.5-4B GGUF + projector | revision pinned in model manifest | Model license and [package card](https://huggingface.co/unsloth/Qwen3.5-4B-GGUF) apply |
| Whisper multilingual small | whisper.cpp revision pinned in SpeechAssets | OpenAI Whisper MIT model/source notices |
| Piper Windows runtime | original 2023.11.14-2 release | Original Piper release notices and bundled component licenses; do not substitute the current project's licensing for this pinned release |
| English lessac voice | pinned piper-voices model | Dataset license linked from preserved MODEL_CARD |
| Persian amir voice | pinned piper-voices model | MODEL_CARD specifies CC0 dataset, fine-tuned from lessac |
| Turkish dfki voice | pinned piper-voices model | MODEL_CARD specifies CC BY-NC-SA 4.0 dataset, fine-tuned from lessac; intended here for personal use |

Voice MODEL_CARD files are retained beside the ONNX models in the user-selected speech model folder. See [native download pins](docs/native.md) and [runtime manifest](docs/runtime.md). Third-party APIs have separate account and subscription terms.

Canvas UI is **not bundled into HeyBuddy**. Its upstream source uses MIT plus Commons Clause; the cross-project helper fetches selected components directly for each compatible web project and preserves notices. [Canvas UI license](https://github.com/DavidHDev/canvas-ui/blob/main/LICENSE.md).

Test-only packages include xUnit, Microsoft.NET.Test.Sdk and DocumentFormat.OpenXml. Tests are not shipped in the desktop package.
