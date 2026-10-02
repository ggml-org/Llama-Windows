# Winget (Windows Package Manager) distribution

`winget install Llama` is the Windows-native install path: no browser, no
"untrusted app" SmartScreen flow for users who install via winget, and
`winget upgrade` keeps the app current alongside the rest of the system.
The MSIX is already signed with Azure Trusted Signing under the Hugging Face
publisher, which is everything winget needs to install and upgrade it.

## Manifests

This folder holds the current winget manifests for the package
`HuggingFace.Llama`, mirroring the layout of the
[winget-pkgs](https://github.com/microsoft/winget-pkgs) repository:

| File | Purpose |
|---|---|
| `HuggingFace.Llama.yaml` | Version manifest — the current package version |
| `HuggingFace.Llama.locale.en-US.yaml` | Default locale — name, publisher, description, tags |
| `HuggingFace.Llama.installer.yaml` | Installer — URL/SHA256 of the release `.msixbundle` |

The published copies live in winget-pkgs under
`manifests/h/HuggingFace/Llama/<version>/`; these templates are the source
of truth the submission starts from.

## One-time setup

1. Install the submission tool: `winget install Microsoft.WingetCreate`.
2. Fill in the two one-time values that only an installed copy can supply
   (they never change afterwards — the package identity is fixed):
   - `PackageFamilyName` in `HuggingFace.Llama.installer.yaml` — after
     installing the app once, read it with
     `powershell Get-AppxPackage *Llama* | Select PackageFamilyName`.
     It is `<IdentityName>_<publisherHash>` and lets winget match an
     installed copy for `winget upgrade`.
   - `License` in `HuggingFace.Llama.locale.en-US.yaml` — match the
     repository's actual license once one is declared.

## Per-release update flow

Every release tag (`v0.13.0`, …) publishes
`LlamaApp-v0.13.0.msixbundle` on the GitHub Release. Update winget with:

```powershell
# Downloads the bundle, computes the SHA256, rewrites the manifests,
# opens a browser to sign in to GitHub and submit the PR to winget-pkgs.
wingetcreate update HuggingFace.Llama `
    --version 0.13.0 `
    --urls "https://github.com/ggml-org/Llama-Windows/releases/download/v0.13.0/LlamaApp-v0.13.0.msixbundle|win-x64" `
    --submit
```

(The `.msixbundle` carries both x64 and ARM64 packages; Windows installs the
matching architecture, so a single x64-declared installer entry covers both.)

Then mirror the generated manifests back into this folder so the repo stays
the source of truth.

## Validating a manifest locally

```powershell
winget validate --manifest .
winget show --manifest .          # renders what users will see
winget install --manifest .       # side-by-side install test
```

`winget validate` enforces the schema (URL reachable, SHA256 matches,
required fields present) — run it before every submission.
