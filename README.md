# LlamaApp

A Windows 11 system-tray app for running [llama.cpp](https://github.com/ggml-org/llama.cpp) models locally — a WinUI 3 port of the macOS [llama.app](https://llama.app) menu-bar app.

The app lives in the notification area. Clicking the tray icon pops a borderless, Mica-backed flyout (anchored to the icon) showing your locally available models and a recommended catalog from the [Hugging Face Hub](https://huggingface.co). It adopts, launches, or auto-installs a `llama` binary and drives it over its local REST API.

## Features

- **Tray-only flyout UI** — no taskbar window; left-click the tray icon to show/hide, right-click for Open/Exit. Mirrors the macOS menu-bar experience on Windows 11.
- **Auto-managed llama.cpp binary** — adopts a running server on `localhost:2276`, launches `%USERPROFILE%\.llama-app\llama.exe`, or downloads it via the official [`install.ps1`](https://llama.app/install.ps1) when none is found. No elevation required.
- **Local models panel ("Available")** — lists models the running server reports via `GET /models`, enriched with catalog metadata (display name, params, size, brand logo, vision flag). Load/unload a model and open its WebUI from a row's action glyph.
- **Recommended Models panel** — a scrollable list of downloadable builds (quants) from the Hub, with a one-click download that streams real-time progress over the server's `GET /models/sse` endpoint.
- **Settings window** — Hugging Face access token (for gated/private repos) and a configurable GGUF cache directory, persisted per-user.
- **Per-user logging** — daily-rotated logs at `%LOCALAPPDATA%\LlamaApp\logs\LlamaApp-YYYYMMDD.log` (7-day retention).

## Solution layout

| Project                   | Target                        | Role                                                                                                     |
|---------------------------|-------------------------------|----------------------------------------------------------------------------------------------------------|
| `LlamaApp`                | `net10.0-windows10.0.19041.0` | WinUI 3 unpackaged app: tray icon, flyout shell, settings window, views.                                 |
| `LlamaApp.LlamaCpp`       | `net10.0`                     | Detects/installs/launches the `llama` binary, talks to the server REST API, parses SSE download streams. |
| `LlamaApp.HuggingFace`    | `net10.0`                     | Hugging Face Hub client and remote catalog fetcher (`https://llama.app/v1/catalog.json`).                |
| `LlamaApp.Catalog`        | `net10.0`                     | Generic catalog layer over `IModelSource` (abstraction shared by Hub + local sources).                   |
| `LlamaApp.Common`         | `net10.0`                     | Shared interfaces (`IModel`, `IModelSource`), logging, `ModelDownloadProgress`.                          |
| `LlamaApp.LlamaCpp.Tests` | `net10.0`                     | xUnit tests for the server's JSON schemas and SSE parsing.                                               |

The helper libraries are pure managed (Any CPU); only the WinUI app builds for a concrete platform (x64/x86/ARM64).

## Build & run

Requires the **.NET 10 SDK** and the Windows App SDK workloads (WinUI 3, WebView2).

```bash
dotnet restore
dotnet build -c Debug
dotnet run -c Debug
```

Build for a specific platform (required for the app to launch correctly):

```bash
dotnet build -c Release -r win-x64      # or win-x86 / win-arm64
```

Run the schema/SSE unit tests:

```bash
dotnet test LlamaApp.LlamaCpp.Tests
```

> Note: Any-CPU builds are coerced to `x64` in the app project so the x64 apphost matches the installed WebView2 runtime (the x64/ARM64EC WebView2 won't load under an ARM64-native apphost).

## Runtime locations

| What | Path |
|---|---|
| App-managed binary | `%USERPROFILE%\.llama-app\llama.exe` |
| GGUF model cache | `%USERPROFILE%\.cache\huggingface\hub` (shared with `llama.cpp` & HF tools) |
| Settings | `%LOCALAPPDATA%\LlamaApp\settings.json` |
| Logs | `%LOCALAPPDATA%\LlamaApp\logs\LlamaApp-YYYYMMDD.log` |
| Server | `http://localhost:2276` (`llama serve --port 2276`) |

## Architecture notes

- `LlamaManager.Shared` is a singleton that owns the binary resolution + server process lifecycle, exposes `StateChanged` / `ModelsChanged` events, and is consumed by both `App` (startup) and `MainWindow` (live UI reconciliation).
- The model-state poller fires `ModelsChanged` roughly every second with a fresh `/models` snapshot; the Available section reconciles rows in place (play → loading ring → open glyph) rather than rebuilding the list, to avoid flicker and lost click state.
- SSE download progress is parsed from the server's `GET /models/sse` stream (`ParseSseStreamAsync`), which dispatches one JSON event per blank line (and flushes a trailing event at EOF).
- The flyout dismisses itself on deactivation (focus loss) with short grace periods to avoid bounce-reopen quirks.