# pad — Spec (v1.0 — signed off; M1 deployed)

A personal web app for storing notes and files, with a VS Code–style interface: file tree on the left, tabbed editor on the right. All data lives in an Azure Storage blob container. Deployed as an Azure Web App on the existing `mikevhplan` App Service plan.

## 1. Goals

- Browse, create, rename, move, and delete files and folders in a tree (full CRUD).
- Edit text/markdown notes in-browser with a real code editor.
- Upload, download, and preview arbitrary files (images, PDFs, etc.).
- Azure Blob Storage is the single source of truth. No database.
- Runs cheaply on the existing plan; no new compute spend.

## 2. Non-goals (v1)

- Search of any kind beyond quick-open by file name (filename/full-text search → v2).
- Markdown live preview, blob versioning/soft delete (deferred; not requested for v1).
- Real-time multi-user collaboration.
- Full VS Code extension ecosystem, terminal, or debugger.
- Version control integration (git). Blob versioning may cover history instead.
- Offline/PWA support.

## 3. Existing infrastructure (verified)

| Item | Value |
|---|---|
| App Service plan | `mikevhplan` |
| Resource group | `mikevh_group` |
| Region | West US (resource group itself is West US 2; web app must be created in West US) |
| SKU | S1 Standard |
| OS | **Windows** (not Linux) |
| Existing web apps in RG | none |
| Existing storage accounts in RG | none (needs creating) |

Implications: the Windows plan means Node (via iisnode) or .NET are the natural runtimes; a Linux-only runtime or container deploy would not work on this plan. S1 supports custom domains, TLS, deployment slots, and Always On.

## 4. Architecture

```
Browser (SPA)  ──HTTPS──▶  Azure Web App "pad" (API + static hosting)
                                   │  Managed Identity (RBAC)
                                   ▼
                       Storage account ▶ blob container "pad"
```

- **Frontend:** single-page app served by the web app. Monaco Editor (the VS Code editor component) plus a custom file tree, tabs, and status bar.
- **Backend:** thin API that proxies to Blob Storage. The browser never sees storage credentials.
- **Auth to storage:** system-assigned managed identity with `Storage Blob Data Contributor` on the container. No connection strings or keys in app settings.
- **Storage:** new StorageV2 account (LRS, Hot tier), private container, hierarchical namespace **off** (see open question 3).

### Stack (decided)

- Backend: ASP.NET Core (.NET LTS) minimal API, `Azure.Storage.Blobs` + `Azure.Identity`, `Fido2NetLib` for passkeys.
- Frontend: React + Vite + TypeScript, `@monaco-editor/react`, a tree component (e.g. `react-arborist`). Responsive (mobile-friendly).
- One repo, one deployable: ASP.NET Core serves the built SPA (static files) and `/api/*`. Windows App Service runs it natively under IIS in-process.

## 5. Storage model

- One container (`pad`). Blob name = file path, e.g. `notes/work/ideas.md`.
- Folders are virtual (blob prefixes). Empty folders are persisted with a zero-byte marker blob `<folder>/.padkeep`, hidden in the UI.
- Content-Type set from the file extension on write.
- Rename/move = server-side copy + delete (blobs have no native rename without HNS). A folder rename copies every blob under the prefix.
- Concurrency: use ETags (`If-Match`) on save. A conflicting save returns 409 and the UI offers reload / overwrite.
- Blob versioning / soft delete: not selected for v1 (can be enabled later without app changes).

## 6. API (draft)

All routes under `/api`, JSON unless noted.

| Method | Route | Purpose |
|---|---|---|
| GET | `/tree?prefix=` | List entries (lazy-load one level at a time) |
| GET | `/files/*path` | Read file content (streams, correct Content-Type) |
| PUT | `/files/*path` | Create/overwrite file (supports `If-Match`) |
| DELETE | `/files/*path` | Delete a file |
| POST | `/folders/*path` | Create folder |
| DELETE | `/folders/*path` | Delete folder recursively |
| POST | `/move` | Rename/move `{from, to}` for files or folders |
| POST | `/upload` | Multipart upload (multiple files, drag-drop). Deferred to M4; `PUT /files/*path` already handles single files up to 100 MB |
| POST | `/auth/register/options`, `/auth/register` | Passkey registration (bootstrap/authenticated only) |
| POST | `/auth/login/options`, `/auth/login` | Passkey assertion, sets session cookie |
| POST | `/auth/logout` | End session |
| GET | `/health` | Health probe |

Large uploads: for files above a threshold, issue a short-lived user-delegation SAS so the browser uploads directly to Blob Storage. Needs CORS on the storage account.

## 7. UI

- **Layout:** activity bar (icons) → sidebar (explorer) → editor group with tabs → status bar. Resizable sidebar.
- **Explorer:** expand/collapse, context menu (New File, New Folder, Rename, Delete, Download, Copy Path), drag-drop to move, inline rename (F2), multi-select.
- **Editor:** Monaco with syntax highlighting by extension, markdown, dirty indicator, Ctrl/Cmd+S to save, optional autosave, multiple tabs.
- **Viewers:** image preview, PDF (browser native), "download" fallback for binaries.
- **Command palette** (Ctrl/Cmd+Shift+P) and quick open (Ctrl/Cmd+P) by file name (matches against the already-loaded tree; no search backend).
- **Mobile:** responsive layout; explorer becomes a slide-over drawer, single editor pane, touch-sized tree rows and long-press context menu, on-screen toolbar for Save/New/Upload.
- **Theme:** dark/light following system, VS Code-like colors.
- **Keyboard shortcuts** mirroring VS Code where practical.

## 8. Authentication & security

The app stores personal data on a public URL, so it must be gated.

- **Single user, passkey (WebAuthn) only. No passwords, no third-party identity provider.** Easy Auth does not offer passkey-only sign-in, so auth is implemented in the app with `Fido2NetLib`.
- Flow: register ceremony (one-time) stores the public credential; login = WebAuthn assertion; server issues an HttpOnly, Secure, SameSite=Strict session cookie (data-protection keys persisted to blob storage so sessions survive restarts/scale). All `/api/*` routes require the session.
- Credential storage: a small JSON blob in a separate private container (`pad-system`), not visible in the file tree.
- **Bootstrap (first passkey):** registration is only open while no credential exists, and requires a one-time setup token (generated at deploy, stored as an app setting / Key Vault reference). After the first passkey is registered, registration is closed; adding more passkeys (backup device) requires an authenticated session.
- Recovery: lose all passkeys → regenerate the setup token via app setting and delete the credential blob (documented runbook). Recommend registering at least two passkeys (e.g. phone + laptop/security key).
- WebAuthn requires a stable origin (RP ID). It is bound to the hostname, so changing the domain later invalidates passkeys.
- Rate-limit auth endpoints.
- HTTPS only, TLS 1.2+, FTP disabled.
- Server validates and normalizes paths (reject `..`, absolute paths, control characters).
- Storage account: public blob access disabled, shared-key access disabled, access via managed identity only.
- Upload size limits and a content-type allowlist for inline rendering (serve unknown types with `Content-Disposition: attachment` to avoid stored XSS).

## 9. Deployment

- Web App `pad-mikevh` (`pad` is taken globally) on `mikevhplan` in `mikevh_group` (default URL `pad-mikevh.azurewebsites.net` if the name is free; public hostname `p.zorgen.io`).
- Runtime: .NET on Windows App Service; Always On enabled; HTTP/2 and WebSockets not required.
- Infrastructure as code: Bicep (storage account, container, web app, role assignment, app settings) so it is reproducible. Deploy with `azd` or `az` CLI.
- App settings: `STORAGE_ACCOUNT_NAME`, `STORAGE_CONTAINER` (no secrets).
- CI/CD: GitHub Actions on push to `main`: build frontend + `dotnet publish`, run tests, deploy with `azure/webapps-deploy`, authenticating via OIDC federated credential (no stored secrets). Bicep deploys run manually.
- Logging: Application Insights (optional but cheap).

## 10. Milestones

1. **Foundation:** Bicep for storage + web app + RBAC; hello-world deployed to `pad`.
2. **Core API:** tree/list, read, write, delete, folders, move; ETag handling; tests. ✅ Built and tested locally (43 tests incl. the blob store against Azurite). **Not deployed**: the API has no auth until Milestone 5, so deploying it would expose your storage publicly. Not yet verified against real Azure: server-side blob copy (used by move) with the managed identity.
3. **UI shell:** layout, explorer tree wired to API, open files in Monaco, save. ✅ Built (React + Vite + Monaco in `web/`, served from `wwwroot`) and verified locally in a browser against Azurite: lazy explorer tree, tabs with dirty markers, Cmd/Ctrl+S save, ETag conflict banner (reload or overwrite), image/PDF/download viewers, resizable sidebar, light/dark theme, phone layout with a slide-over explorer. New file/folder use simple prompts; the real context menu is M4. Not deployed (no auth yet).
4. **File management UX:** context menu, drag-drop move, rename, upload, download, viewers. ✅ Built and verified locally against Azurite: right-click menu (New File/Folder, Upload, Download, Rename, Move to…, Copy Path, Delete), inline create and rename (F2), multi-select (Ctrl/Cmd and Shift click), keyboard navigation and Delete, drag-drop to move (hover over a collapsed folder to open it), drop files or folders from the desktop to upload, an upload button, `?download=1` on the API. Open tabs follow renames/moves and close on delete. Phone: long-press opens the menu and **Move to…** replaces dragging. Not included: downloading a folder as a zip. Not verified: a real mouse drag in a browser (the test tool can't start one; handlers were exercised with dispatched drag events), long-press on a real touch device, and dropping folders from the desktop. Not deployed (no auth yet).
5. **Auth lockdown:** ✅ Passkey-only sign-in built and verified locally with a virtual authenticator (registration with setup token, wrong-token rejection, reload keeps session, sign out, sign in again, registration closed afterwards, add/remove passkeys, last passkey can't be removed). Everything under `/api` except `/api/health` and the sign-in endpoints returns 401 without a session (tested). Discoverable passkeys with user verification required; session cookie is HttpOnly + SameSite=Strict (+ Secure outside local dev), 14-day sliding; auth endpoints rate-limited (30/min/IP); data-protection keys and credentials stored in the private `pad-system` container. **Not yet deployed.** Not verified: a real device passkey (Touch ID/phone) against `p.zorgen.io`.
6. **Quick open:** ✅ Cmd/Ctrl+P (overrides the browser's print shortcut, so printing from inside pad is gone) and Cmd/Ctrl+K open a fuzzy file finder; 🔍 button for phones; backed by `GET /api/paths` (flat recursive file list, fetched each time it opens). Open files come first on an empty query; picking a file reveals it in the explorer. Verified locally in a browser; **not yet deployed**; not verified: that a real Chrome/Safari/Firefox cancels the print dialog (headless can't show it), or performance with thousands of files. Command palette (actions) and further shortcuts: not built. Markdown preview and search are deferred to v2 per earlier decisions.
7. **Hardening:** soft delete/versioning, limits, error states, backup notes.

## 11. Decisions and remaining questions

**Decided**
- Single user, passkey-only auth, implemented in-app (Fido2NetLib).
- .NET + React.
- Search is v2; v1 has quick-open by name only.
- Mobile-friendly UI; GitHub Actions auto-deploy.
- Markdown preview and versioning/soft delete not in v1.

**Also decided**
- Passkey bootstrap/recovery as in section 8.
- Hostname: **`p.zorgen.io`** (custom domain on the web app, free App Service managed certificate; passkey RP ID = `p.zorgen.io`). Needs a CNAME `p` → `pad-mikevh.azurewebsites.net` and a TXT `asuid.p` verification record at the DNS host for `zorgen.io`.
- Hierarchical namespace off; files up to ~100 MB proxied through the API (no SAS/CORS in v1). Set request size limits accordingly (Kestrel/IIS `maxAllowedContentLength`).
- GitHub repo: decide later (develop locally first).
- Azure: OK to create resources in `mikevh_group` after final spec sign-off; I will show the Bicep and confirm before deploying.

- Storage account: **`padstoremikevh`** (Standard LRS, StorageV2, HNS off).
- DNS for `zorgen.io` is at Namecheap (not Azure DNS), so you add the CNAME `p` → `pad-mikevh.azurewebsites.net` and TXT `asuid.p` records yourself; I'll give exact values once the web app exists.

**Still open:** none. Spec is ready for final sign-off.

## 12. Next step

Once the open questions are settled I will finalize v1.0 of this spec, then start at Milestone 1 (infrastructure) and proceed in order. No Azure resources will be created until you confirm.
