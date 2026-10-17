# Publishing to the Microsoft Store from GitHub Releases

The release pipeline (`release.yml`) submits the signed `.msixbundle` to the
Microsoft Store after the GitHub Release is created: push a `v*` tag → tests +
MSIX build → Trusted Signing → `.msixbundle` → GitHub Release → **Partner
Center submission**.

The Store job is gated twice: it only runs for version tags, and it waits on
the `microsoft-store` GitHub environment (see below) so a human approves every
submission before it enters certification.

The plumbing uses Microsoft's official
[Store Developer CLI (`msstore`)](https://github.com/microsoft/msstore-cli),
installed by the official
[`microsoft-store-apppublisher`](https://github.com/microsoft/microsoft-store-apppublisher)
setup action (pinned to a commit SHA like every other action in this repo —
the action's version governs which CLI lands on the runner).

## One-time setup

### 1. Partner Center

- A [Partner Center](https://partner.microsoft.com/dashboard) developer
  account for the publishing org.
- Reserve/associate the app so the package identity matches: the manifest's
  `<Identity Name="4e440e29-8151-4465-b20f-fb3acb283925" …>` must be the
  Store-reserved identity name for the product. Keep the Name/Version rules in
  `Package.appxmanifest` in mind — changing the identity breaks upgrades.
- **Signing**: the Store accepts developer-signed desktop MSIX. The current
  manifest publisher is the Azure Trusted Signing subject ("CN=Hugging Face,
  …") — keep Trusted Signing and associate that exact publisher with the
  product in Partner Center ("Package identity" on the submission), or let the
  Store sign instead (then the manifest publisher must become the Store
  account publisher — a deliberate change, see the manifest comment).
- Note the product's **Store ID** (GUID from the app's Partner Center page) —
  it becomes `PARTNER_CENTER_APP_ID`.

### 2. Entra app for the submission API

Partner Center → *Settings → Developer Center API access*: create/associate an
Entra (Azure AD) application and grant it the **Manager (developer)** role for
the account ("Manage Store submissions" via the Partner Center API). From that
app you need:

- the **Application (client) ID** → `PARTNER_CENTER_CLIENT_ID`
- the **Directory (tenant) ID** → `PARTNER_CENTER_TENANT_ID`
- a **client secret** → `PARTNER_CENTER_CLIENT_SECRET`

### 3. GitHub configuration

Repository secrets:

| Secret | Value |
|---|---|
| `PARTNER_CENTER_SELLER_ID` | Partner Center seller/account id (integer, account settings) |
| `PARTNER_CENTER_TENANT_ID` | Entra directory (tenant) id |
| `PARTNER_CENTER_CLIENT_ID` | Entra application (client) id |
| `PARTNER_CENTER_CLIENT_SECRET` | Entra client secret |
| `PARTNER_CENTER_APP_ID` | the app's ID from Partner Center (the identifier the submission API addresses) |

Repository variable (optional):

| Variable | Effect |
|---|---|
| `STORE_NO_COMMIT = true` | keep the submission as a **draft** in Partner Center instead of committing it to certification — use for the first runs |

Environment: create a `microsoft-store` environment (Settings → Environments)
with **required reviewers**. That is the human gate in front of every Store
submission.

## How the non-interactive auth works

`msstore` normally initializes interactively (`msstore init`). For CI, the
officially supported path is `msstore reconfigure` with the credentials as
flags — exactly what the `microsoft-store-apppublisher` action's own example
does:

```
msstore reconfigure --tenantId … --sellerId … --clientId … --clientSecret …
msstore publish <signed .msixbundle> --appId <PARTNER_CENTER_APP_ID> [--noCommit]
```

Under the hood `reconfigure` writes the CLI's settings
(`%LOCALAPPDATA%\Microsoft\MSStore.CLI\settings.json`) and stores the client
secret where the CLI's credential manager reads it — if you ever see
`We could not find credentials that match your configurations. / Secret is
empty`, the reconfigure step didn't run or its secret was empty.

`msstore publish` creates a new Partner Center submission, uploads the
package, and commits it to certification (unless `--noCommit`).

## First-run checklist

1. Set `STORE_NO_COMMIT=true`.
2. Push a tag (or re-run the `store` job of an existing release via the
   Actions UI — it downloads the release's bundle artifact).
3. In Partner Center: verify the draft submission — package list, rollout,
   pricing, notes.
4. Commit the submission there manually, or unset `STORE_NO_COMMIT` and let
   the pipeline commit from the next release on.

## Notes & knobs

- **Certification**: every committed submission goes through Store
  certification; failed certification blocks the release from the Store (the
  GitHub Release still exists).
- **Gradual rollout**: `msstore publish` supports
  `--packageRolloutPercentage` (0–100) — add it to the last step if you want
  staged Store rollouts.
- **Package format**: the job feeds the `.msixbundle` the release produces.
  If Partner Center wants a `.msixupload` for a particular flow, `msstore
  package` can produce the Store-ready package from the build output — see
  the [CLI docs](https://aka.ms/msstoredevcli/docs).
- **Flights**: `msstore flights`/`--flightId` can target Store flight groups
  instead of the retail submission if you later want Insider-style
  distribution.
