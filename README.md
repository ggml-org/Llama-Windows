# Llama

**Local LLMs, one click away — right from your Windows system tray.**

Llama is a Windows 11 tray app for running [llama.cpp](https://github.com/ggml-org/llama.cpp) models locally. It's a WinUI 3 port of [Llama for Mac](https://github.com/ggml-org/Llama-macOS).

<img width="1310" height="737" alt="Tray flyout showing available and recommended models" src="https://github.com/user-attachments/assets/6d726939-5c81-4702-a7a4-f28a14ce4876" />

## Install

Grab the latest `.msixbundle` (x64 + ARM64) from [**Releases**](https://github.com/ggml-org/Llama-Windows/releases) and double-click to install.

## Highlights

- **Lives in your tray** — a borderless, Mica-backed flyout anchored to the tray icon
- **Zero-setup llama.cpp** — uses your existing `llama.exe`, or downloads one for you
- **One-click models** — browse and download recommended models from the Hugging Face Hub
- **Model details** — click any model for a context-length picker with per-option memory estimates (read from the GGUF header), remembered per model, plus chat, copy-model-ID, curl-request, and Hugging Face actions
- **Standard storage** — models live in the Hugging Face cache, shared with `llama.cpp` and other tools
- **Spotlight-like overlay** — press `Alt+Space` from any app to chat with your loaded model

## The overlay

`Alt+Space` from any app opens a borderless window with the WebUI for whichever model is loaded. It hides instead of closing, so re-summoning is instant and your conversation is still there.

<p align="center">
  <img src=".images/overlay_streaming.gif" alt="Streaming a response in the overlay" width="640">
</p>

## How it works

Llama manages a `llama serve` process and talks to it over its REST API. Your models stay in the standard Hugging Face cache (`%USERPROFILE%\.cache\huggingface\hub`), shared with `llama.cpp` and HF tooling. Settings and logs live under `%LOCALAPPDATA%\Llama`.

## Telemetry

Llama sends anonymous usage statistics to Hugging Face (same idea as [text-generation-inference](https://github.com/huggingface/text-generation-inference)'s telemetry, tagged `app: "llamawin"`), to understand which hardware the community runs local models on:

- **What is sent** — a random per-install id (a GUID stored in `settings.json`, unlinkable to you), the app version, Windows version, CPU name and core count, total RAM, and installed GPU names.
- **When** — once when the app starts (`start`), every 30 minutes while it runs (`ping`), and once on a clean exit (`stop`). POSTed to `https://huggingface.co/api/telemetry/llamacpp`.
- **What is never sent** — prompts, model names, model files, or any file contents. Inference stays 100% local.

To opt out, either:

- untick **Usage telemetry** in Settings (turning it off takes effect immediately; turning it back on needs an app restart), or
- set the environment variable `LLAMA_WINDOWS_TELEMETRY=OFF` before launching Llama (an explicit `ON` or any other/unset value falls back to the setting above).

## Building from source

You'll need the **.NET 10 SDK** with the Windows App SDK workload:

```bash
dotnet build -c Release -r win-x64   # or win-arm64
dotnet test                          # run the unit tests
```

---

Made with 🦙 for Windows 11 · Models run 100% locally — your prompts never leave your machine.
