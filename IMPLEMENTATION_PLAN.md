# AJBrowser — Implementation Plan

> Living roadmap for AJBrowser, a Windows desktop browser built with C#, WPF and WebView2.
> Tick the checkboxes as work lands. See [How to continue with the coding agent](#13-how-to-continue-with-the-coding-agent) at the end.

**Plan version:** 1.2 · **Created:** 2026-11-26 · **Current phase:** Phase 0 done · Phase 0.5 done · Phases 1, 2, 2.5 (ad blocking) and 3 (bookmarks, history, settings, session) implemented, awaiting your manual checks · Phase 4 (download manager) **built** (4a–4c; key checks run by the agent in the real app) · Phase 5 in progress: checkpoint **5a done** (core media detection), next **5b**

---

## Table of contents

1. [Project findings](#1-project-findings)
2. [Assumptions](#2-assumptions)
3. [Technology evaluation and dependency decisions](#3-technology-evaluation-and-dependency-decisions)
4. [Feasibility assessment](#4-feasibility-assessment)
5. [Architecture](#5-architecture)
6. [Key technical designs](#6-key-technical-designs)
7. [Phased roadmap](#7-phased-roadmap)
8. [Known limitations (by design)](#8-known-limitations-by-design)
9. [Deferred and optional features](#9-deferred-and-optional-features)
10. [Items to verify](#10-items-to-verify)
11. [Spike findings log](#11-spike-findings-log)
12. [Decision log](#12-decision-log)
13. [How to continue with the coding agent](#13-how-to-continue-with-the-coding-agent)

---

## 1. Project findings

Inspected on 2026-11-26.

| Item | Finding |
|---|---|
| Solution | `AJBrowser.slnx` (new XML solution format) with one project: `AJBrowser/AJBrowser.csproj` |
| Project SDK | `Microsoft.NET.Sdk` with `<UseWPF>true</UseWPF>` |
| Target framework | `net10.0-windows`, `OutputType=WinExe`, `Nullable=enable`, `ImplicitUsings=enable` |
| Packages | **`Microsoft.Web.WebView2` 1.0.4370.26 is installed**: it is referenced in the csproj, restored in `obj/project.assets.json`, and `Microsoft.Web.WebView2.Core/Wpf.dll` are in `bin/Debug/net10.0-windows`. It isn't used in code yet. |
| Startup | `App.xaml` uses `StartupUri="MainWindow.xaml"`. `MainWindow` is an empty `Grid`, titled "MainWindow", 800×450. `App.xaml.cs` is empty. |
| Other files | `AssemblyInfo.cs` (standard `ThemeInfo`), `AJBrowser.csproj.user` (empty, git-ignored) |
| Repository instructions | None (no `CLAUDE.md`, `README.md`, `global.json`, or `Directory.Build.props`) |
| Git | `main` branch, one commit (`1670bb2 initial commit`). A `.gitignore` for VS/.NET exists. |
| Installed SDKs | .NET SDK **11.0.100** (plus a legacy 2.1 SDK). There is **no .NET 10 SDK**, but the .NET 11 SDK builds `net10.0` targets. |
| Installed runtimes | `Microsoft.WindowsDesktop.App` 10.0.15 and 11.0.0, so the app can run on .NET 10. |
| Build check | `dotnet build AJBrowser.slnx` **succeeded with 0 warnings and 0 errors** (run by the agent on 2026-11-26). The agent did **not** launch the app. You reported that it opens. |
| Tests | None |

**Notes**
- Keep targeting .NET 10 (LTS). The .NET 11 SDK is fine for building. If you want reproducible builds, add a `global.json` in Phase 0 that pins the SDK feature band, or allow roll-forward. That's optional.
- The template's unused `using` directives in `App.xaml.cs` and `MainWindow.xaml.cs` are harmless. Clean them up when those files are first edited.

---

## 2. Assumptions

These are documented guesses. Change them here if any are wrong.

1. **Platforms:** Windows 10 (supported builds) and Windows 11, **x64 first**. ARM64 is deferred.
2. **Distribution:** a personal or small-audience desktop app distributed as a direct download, not the Microsoft Store (for now).
3. **Licensing of AJBrowser itself:** not decided. Dependency choices below flag copyleft implications (GPL/MPL/LGPL) so the choice stays open.
4. **Single user profile** to start ("Default"), with private windows later. Multiple named profiles are deferred.
5. **Legal use:** the video downloader is for content the user has the right to download. DRM circumvention is out of scope, and so is bypassing paywalls or site authentication.
6. **Developer workflow:** Visual Studio on Windows. The agent works in the repo, and you verify behaviour in VS.
7. **No telemetry.** Nothing is sent anywhere except the pages the user browses, filter-list downloads, and (later) update checks.

---

## 3. Technology evaluation and dependency decisions

| Proposed | Verdict | Notes |
|---|---|---|
| C# / .NET 10 LTS | ✅ Keep | Already targeted. LTS gives a long support window. |
| WPF + XAML | ✅ Keep | Mature, and it hosts WebView2 well. One significant constraint is **"airspace"**: WebView2 renders into its own HWND, so ordinary WPF elements **cannot be drawn on top of the web content**. The UI must use `Popup` (which gets its own HWND) for flyouts, or panels docked beside the web view. This shapes the download panel and video picker design. |
| WebView2 (Evergreen) | ✅ Keep | Installed. Evergreen means Microsoft updates the engine. WebView2 is **not Chrome**: it has no extension store UI, no built-in ad blocker, and some features need newer SDK or runtime versions. |
| CommunityToolkit.Mvvm | ✅ Add in Phase 0 | MIT, small, source-generator based (`[ObservableProperty]`, `[RelayCommand]`). The closest WPF analogue to React state hooks, with little boilerplate. |
| Microsoft.Data.Sqlite | ✅ Added in Phase 3 (11.0.0, Core) | MIT. Use plain SQL with small repository classes. **No EF Core** (heavy, adds migration tooling, overkill here) and **no Dapper** for now (optional later). |
| HttpClient | ✅ In-box | Used for app-managed downloads, manifest fetching, and filter-list updates. Use one shared `SocketsHttpHandler`-based client per profile context. |
| FFmpeg / FFprobe | ✅ Add in Phase 6 (not earlier) | Run as an **external process**, never linked. Use an **LGPL build** (no `--enable-gpl`), because we only need remuxing, no x264 encoding. Distribution details are in Phase 10. Direct MP4/WebM downloads (Phase 5) don't need FFmpeg. |
| DI container | ❌ Not initially | Use a hand-written composition root (`AppServices`) built in `App.OnStartup`. It's easier to read for someone new to C#. Revisit `Microsoft.Extensions.DependencyInjection` if wiring becomes painful. |
| Logging framework | ❌ Not initially | A small in-house `ILogSink` that writes to a rolling file, with a **mandatory redaction helper**. Avoids Serilog/NLog until needed. |
| Theme library | ⚖️ Decide in Phase 0 | Option A (recommended first): WPF's in-box **Fluent theme** (`Application.ThemeMode` = Light/Dark/System). It still carries the `[Experimental]` diagnostic **WPF0001** in .NET 9/10, so it must be suppressed. Option B: custom resource dictionaries only. Option C: WPF-UI (lepoco, MIT) if A proves too buggy. Our custom chrome (tabs, toolbar) uses our own semantic brush keys either way, so switching later is cheap. |
| Icons | ✅ In-box | Segoe Fluent Icons (Windows 11) with a Segoe MDL2 Assets fallback (Windows 10). No icon package needed. |
| Ad-block engine | ✅ Decided 2026-11-26 | **uBlock Origin Lite** (GPLv3, bundled unmodified) loaded through WebView2's extension API in Phase 2.5; it blocked YouTube ads in the spike (F15). **adblock-rust** (Brave, MPL-2.0) via a native shim stays the optional later upgrade (Phase 8). Built-in **WebView2 tracking prevention** remains the baseline. |
| Public Suffix List | ⏳ Only if needed | Needed for "registrable domain" (eTLD+1) logic, such as per-site toggles and third-party checks. adblock-rust handles this internally. If we need it ourselves, use a maintained MIT library or bundle the PSL with periodic updates. |
| Test framework | ✅ Add in Phase 0 | xUnit with `AJBrowser.Core.Tests`. Pure-logic tests only; UI/WebView2 behaviour is verified manually. |
| Installer / updater | ⚖️ Decide in Phase 10 | Leading candidate: **Velopack** (MIT; installer, delta updates, GitHub Releases hosting). Alternatives: MSIX (virtualised AppData quirks, Store-friendly) or Inno Setup (no updater). |

**Recommended changes from the proposal:** none to the core stack. The refinements are: (1) split pure logic into a `AJBrowser.Core` class library for testability; (2) defer FFmpeg until HLS; (3) postpone choosing an ad-block engine until the spike measures real behaviour; (4) skip EF Core, DI containers and logging frameworks for now.

---

## 4. Feasibility assessment

### Straightforward (well-supported WebView2/.NET APIs)
- Navigation, address bar, back/forward/reload/stop/home (`CoreWebView2.Navigate`, `GoBack`, `Reload`, `Stop`, `NavigationStarting/Completed`, `SourceChanged`, `HistoryChanged`).
- Titles and favicons (`DocumentTitleChanged`, `FaviconChanged`, `GetFaviconAsync`).
- Multiple tabs, using one WebView2 control per tab that shares a single `CoreWebView2Environment`.
- New-window handling (`NewWindowRequested` with deferral and `args.NewWindow`, which keeps `window.opener` working for OAuth popups).
- Browser downloads with progress, pause/resume (where `CanResume`), cancel and custom paths (`DownloadStarting`, `CoreWebView2DownloadOperation`).
- Settings, bookmarks, history and persistence (SQLite / JSON).
- Zoom, print, fullscreen video (`ZoomFactor`, `ShowPrintUI`, `ContainsFullScreenElementChanged`).
- Private browsing (`CoreWebView2ControllerOptions.IsInPrivateModeEnabled`) and named profiles (`ProfileName`).
- Process-failure recovery (`ProcessFailed`) and runtime-missing detection (`GetAvailableBrowserVersionString`).
- Baseline tracking protection (`CoreWebView2Profile.PreferredTrackingPreventionLevel`).
- Direct HTTP(S) file downloads with Range-based resume through HttpClient.

### Substantial (doable, but real engineering)
- **Media detection**: classifying responses (`WebResourceResponseReceived`), deduplicating, suppressing segments, scoping to tab and navigation, and transferring headers and cookies safely.
- **HLS**: master/media playlist parsing, a segment downloader with retries and AES-128 (clear-key) decryption, then FFmpeg remux.
- **DASH**: MPD parsing (`SegmentTemplate`, `SegmentTimeline`, `SegmentBase`, `SegmentList`), separate audio/video download, FFmpeg merge, container selection.
- **Ad blocking**: a production filter engine (EasyList syntax), network interception on the UI thread with a strict latency budget, cosmetic filtering per frame, list updates and caching.
- **Download manager**: queueing, concurrency limits, crash-safe partial files, persistence across restarts.
- **Polished tab bar** (drag reorder, tabs in the title bar via `WindowChrome`) and theming.
- **Packaging**: installer, code signing, updates, FFmpeg licensing compliance.

### Limitations (cannot be fully solved; communicate them in the UI)
- **DRM** (Widevine/PlayReady/FairPlay) content: never downloadable. We detect `ContentProtection` / `SAMPLE-AES` and say so.
- **Not every site works.** Sites that obfuscate stream URLs, use one-time tokens, WebSockets, or custom players may not be detectable or downloadable. Large platforms such as YouTube use adaptive ranged requests with throttling and signature schemes that need site-specific extractors (see §9).
- **Blob URLs** (`blob:`) aren't files; see §6.3.
- **Ad blocking gaps:** first-party ads, server-side ad insertion in video streams, anti-adblock walls, WebSocket traffic, and any requests WebView2 doesn't surface to the host.
- **WebView2 isn't Chrome:** some Chrome/Edge browser UI features (extension popups, sync, password manager UI parity, built-in PDF editor features) are absent or differ.
- **Live streams** (HLS without `#EXT-X-ENDLIST`) aren't supported initially.

---

## 5. Architecture

### 5.1 Solution layout

```
AJBrowser.slnx
├─ AJBrowser/                      WPF app (net10.0-windows) — UI + WebView2 glue
│  ├─ App.xaml(.cs)                Composition root (AppServices), startup/shutdown, global error handling
│  ├─ Views/                       XAML windows, user controls, popups
│  │  ├─ MainWindow.xaml           Tab strip, toolbar, content host, side panels
│  │  ├─ Controls/                 TabStrip, AddressBar, DownloadPanel, VideoPicker, …
│  │  └─ Pages/                    Settings, History, Bookmarks, Downloads (internal pages)
│  ├─ ViewModels/                  MainWindowViewModel, TabViewModel, DownloadsViewModel, …
│  ├─ Browser/                     WebView2-specific code (only place that touches CoreWebView2)
│  │  ├─ BrowserEnvironment.cs     Creates/owns the shared CoreWebView2Environment(s)
│  │  ├─ BrowserTab.cs             Wraps one WebView2 control + its events → plain C# events
│  │  ├─ DownloadInterceptor.cs    DownloadStarting → DownloadManager
│  │  ├─ MediaSniffer.cs           WebResourceResponseReceived → media pipeline (Phase 5+)
│  │  ├─ RequestBlocker.cs         WebResourceRequested → filter engine (Phase 8)
│  │  └─ Scripts/                  Injected JS (video hints, cosmetic filtering) as embedded resources
│  ├─ Services/                    App services needing WPF/Windows (dialogs, theme, clipboard, shell open)
│  ├─ Themes/                      Light.xaml, Dark.xaml, Controls.xaml (semantic brushes/styles)
│  └─ Assets/                      App icon, images
│
├─ AJBrowser.Core/                 Class library (net10.0) — NO WPF, NO WebView2 references
│  ├─ Navigation/                  UrlOrSearchResolver, SearchEngine definitions
│  ├─ Persistence/                 Database, migrations, repositories (History, Bookmarks, Downloads, Sites)
│  ├─ Settings/                    AppSettings model + JSON store
│  ├─ Downloads/                   DownloadJob, DownloadQueue, HttpFileDownloader, FileNamer
│  ├─ Media/                       MediaCandidate, classifier, HlsParser, DashParser, MediaDownloadPlanner
│  ├─ Media/Ffmpeg/                FfmpegRunner, ContainerSelector, progress parser
│  ├─ Blocking/                    IFilterEngine, FilterListUpdater, SiteBlockingPolicy
│  ├─ Security/                    Redactor, RequestContext (scoped headers/cookies)
│  └─ Infrastructure/              AppPaths, ILogSink, Clock abstraction
│
└─ AJBrowser.Core.Tests/           xUnit tests for AJBrowser.Core
```

**Why two projects, not one or many:** `Core` holds the logic that can and should be unit-tested (parsers, naming, URL rules, download engine, filter policy) and forbids accidental UI coupling. More projects (Domain, Application, Infrastructure, and so on) would be excessive for a solo developer.

### 5.2 Layer responsibilities

| Layer | Responsibility | Talks to |
|---|---|---|
| Views (XAML) | Layout, bindings, visual states. Minimal code-behind (only things WPF forces, like hosting the WebView2 control). | ViewModels |
| ViewModels | UI state and commands (`ObservableObject`, `RelayCommand`). No WebView2 types. | Browser layer (through `BrowserTab` events/methods), Core services |
| Browser layer | The **only** code that references `CoreWebView2`. Translates WebView2 events into plain C# events and data records. Runs on the UI thread. | WebView2, Core services |
| Core services | Business logic, background work, persistence. UI-agnostic, async, cancellable. | SQLite, file system, HttpClient, FFmpeg process |

### 5.3 Core objects (minimum set)

**ViewModels**
- `MainWindowViewModel`: tab collection, active tab, commands (new/close/reopen tab, shortcuts), panel visibility.
- `TabViewModel`: URL, title, favicon, `IsLoading`, `CanGoBack/Forward`, error state, blocked count, detected media count, `IsPrivate`.
- `AddressBarViewModel` (or part of `TabViewModel`): edit text vs. display URL, suggestions (later).
- `DownloadsViewModel` / `DownloadItemViewModel`: list, progress, commands (pause/resume/cancel/retry/open/show in folder).
- `MediaPickerViewModel`: detected media for the active tab, variants, the download action.
- `SettingsViewModel`, `HistoryViewModel`, `BookmarksViewModel`.

**Services (Core unless noted)**
- `AppPaths`: all folder locations (§5.6).
- `SettingsStore`: load/save `settings.json`, change notifications.
- `Database` + repositories: `HistoryRepository`, `BookmarkRepository`, `DownloadRepository`, `SiteSettingsRepository`, `SessionRepository`.
- `UrlOrSearchResolver`: turns address-bar input into a URI.
- `DownloadManager`: single owner of all download jobs (both kinds), queue, concurrency, persistence.
- `HttpFileDownloader`: Range-capable, resumable HTTP downloads.
- `MediaDetectionService`: per-tab candidate stores, dedupe, manifest resolution (background `Channel<T>`).
- `HlsParser`, `DashParser`, `ContainerSelector`, `FfmpegRunner`.
- `IFilterEngine` + `FilterListUpdater` + `BlockingPolicy` (global/per-site toggles).
- `BrowserEnvironment` (Browser layer): creates environments and profiles once.
- `ThemeService`, `DialogService`, `ShellService` (open file/folder) (App layer).

### 5.4 Major data flows

**Navigation**
```
AddressBar Enter → TabViewModel.Navigate(text)
  → UrlOrSearchResolver → BrowserTab.Navigate(uri) → CoreWebView2
  ← NavigationStarting/SourceChanged/TitleChanged/NavigationCompleted
  ← BrowserTab raises C# events → TabViewModel updates bound properties
  → on success (non-private): HistoryRepository.AddVisitAsync (background)
```

**Browser-managed download**
```
Page triggers download → CoreWebView2.DownloadStarting
  → DownloadInterceptor: args.Handled = true (hide Edge flyout), choose ResultFilePath via FileNamer
  → DownloadManager.TrackBrowserDownload(operation)  [owner: WebView2 runtime]
  ← operation.BytesReceivedChanged / StateChanged → throttled UI updates + DB row
```

**Media detection → app-managed download**
```
CoreWebView2.WebResourceResponseReceived (UI thread, cheap filtering only)
  → MediaSniffer extracts {tabId, navigationId, url, status, content-type, length/range, resourceContext}
  → Channel<MediaObservation> → MediaDetectionService (background)
       classify → dedupe → (if manifest) fetch+parse variants → candidate store per tab
  ← TabViewModel.DetectedMediaCount (marshalled to UI thread)
User picks a variant → MediaDownloadPlanner builds a job (direct | HLS | DASH)
  → RequestContext captured (allowlisted headers + cookies for that exact URL from that tab's profile)
  → DownloadManager enqueues an app-managed job [owner: AJBrowser]
  → HttpFileDownloader / segment downloader → (optional) FfmpegRunner remux → final file
```

**Ad blocking (network)**
```
CoreWebView2.WebResourceRequested (UI thread, MUST be fast)
  → BlockingPolicy.IsEnabledFor(tab top-level site)?  (cached per navigation)
  → IFilterEngine.Match(url, sourceUrl, resourceType) → block?
  → args.Response = env.CreateWebResourceResponse(null, 403?, "Blocked", "")  or  leave untouched
  → per-tab blocked counter++ (batched UI update)
```

### 5.5 Threading model
- All `CoreWebView2` calls and events happen on the **UI thread**. Event handlers do minimal work, then hand off.
- Background work uses `async`/`await`, `Task.Run` for CPU-bound parsing, and `System.Threading.Channels` for event streams.
- UI updates from the background go through `Dispatcher`/`SynchronizationContext`. Progress is **throttled** (about 4 updates per second per download).
- Every long operation takes a `CancellationToken`. Closing a tab cancels its detection work. App shutdown cancels or persists downloads.

### 5.6 Storage locations (per-user, never next to the exe)

| Data | Location |
|---|---|
| WebView2 user data folder (cookies, cache, site storage) | `%LOCALAPPDATA%\AJBrowser\WebView2\` (set explicitly; the default is next to the exe, which breaks when installed under Program Files) |
| SQLite database | `%LOCALAPPDATA%\AJBrowser\Data\ajbrowser.db` (WAL mode) |
| Settings | `%LOCALAPPDATA%\AJBrowser\Data\settings.json` |
| Filter lists + compiled cache | `%LOCALAPPDATA%\AJBrowser\Filters\` |
| Logs (redacted) | `%LOCALAPPDATA%\AJBrowser\Logs\` (rolling, size-capped) |
| Temp segments / partial media | `%LOCALAPPDATA%\AJBrowser\Temp\` (cleaned on startup) |
| Downloaded files | The user's Downloads folder (Known Folder `FOLDERID_Downloads`), configurable |
| FFmpeg (if downloaded on demand) | `%LOCALAPPDATA%\AJBrowser\Tools\ffmpeg\<version>\` |

`%LOCALAPPDATA%` (not roaming) is used because the WebView2 profile and caches are large and machine-specific.

### 5.7 Tabs and profiles
- **One `CoreWebView2Environment` per user data folder**, created once at startup and shared by all normal tabs. All WebView2 controls in one environment must use compatible options, so create them through `BrowserEnvironment` only.
- **Profile "Default"** for normal tabs. **Private windows** use `CoreWebView2ControllerOptions { IsInPrivateModeEnabled = true }`. Their cookies and cache stay in memory and are discarded when the last private WebView closes. Private tabs: no history entries, no session restore, downloads not persisted to history after the session (the file stays on disk).
- Each tab has a stable `TabId` (GUID). Each top-level document load gets a `NavigationId` (from `NavigationStarting.NavigationId`). Media detections and blocked counts are keyed by `(TabId, NavigationId)`.
- Cookies for app-managed downloads come from **that tab's profile** `CookieManager`. The private profile's cookies never go to normal downloads, and the reverse.
- Background tabs can later be suspended (`TrySuspendAsync`) or lowered with `MemoryUsageTargetLevel`.

### 5.8 Security model
- **No host objects** (`AddHostObjectToScript`) are exposed to web content, ever.
- Injected helper scripts talk to the host via `chrome.webview.postMessage`. **Treat every web message as untrusted**: any page script can send the same messages. Validate schema and size, and never perform privileged actions (file writes, process launches) based solely on page messages. Messages may only *hint* (for example, "this `<video>` reports 1280×720"); the user confirms any action.
- Never disable certificate validation. Leave `ServerCertificateErrorDetected` at default behaviour (block with an error page). Our HttpClient uses default validation.
- Never log cookies, `Authorization`, `Set-Cookie`, full query strings of media URLs, or signed URLs. `Redactor.Url(uri)` strips query and fragment for logs and history display of media.
- Signed or expiring media URLs and captured headers stay **in memory only**. Persisted download records store the page URL, a redacted media URL, and the final file path.
- Downloaded files keep Windows Mark-of-the-Web: WebView2 browser downloads already do. For app-managed downloads, write a `Zone.Identifier` ADS (a later polish item) so SmartScreen protections still apply.
- FFmpeg is launched with `ProcessStartInfo.ArgumentList` (no shell string concatenation), only with local file paths that we created. Cookies are never passed on the command line.
- Leave WebView2 security features (Safe Browsing / SmartScreen, sandbox, site isolation) at their defaults.

---

## 6. Key technical designs

### 6.1 Download ownership

| | **Browser-managed download** | **App-managed download** |
|---|---|---|
| Triggered by | Page navigation/link/`Content-Disposition` → `DownloadStarting` | User picks a detected media item (or later, "Download with AJBrowser") |
| Engine | WebView2 runtime (browser process) | AJBrowser `HttpClient` / segment downloader / FFmpeg |
| Cookies/auth | Handled natively by the browser | Captured from the tab's profile, scoped to the target URL |
| Pause/Resume | `operation.Pause()` / `Resume()` only when `CanResume` is true | HTTP Range when the server sends `Accept-Ranges: bytes` and a stable `ETag`/`Last-Modified`. Segmented HLS/DASH resumes at segment granularity. |
| Survives app restart | No. In-progress operations die with the browser process and are marked "Interrupted" in history. User can retry (re-navigates the URL; may fail for one-time links). | Partially. Jobs and partial files persist, and resume after restart when the server supports it. |
| Survives tab close | **To verify in spike** (§10) | Yes (owned by `DownloadManager`, not the tab) |
| Filename/folder | We set `ResultFilePath` before completion | We choose |

`DownloadManager` presents both kinds in one list through a common `DownloadItem` model with a `Kind` field. A small badge explains limitations ("Pause not supported by this server", "Browser download — can't resume after restart").

**File naming rules (`FileNamer`, Core, unit-tested):** sanitise invalid chars and reserved names (`CON`, `NUL`, …), trim trailing dots and spaces, cap length, de-duplicate as `name (1).ext`, `name (2).ext`. Write to `name.ext.ajpart` and atomically rename on completion. Never overwrite without explicit confirmation.

### 6.2 Media detection pipeline

1. **Observe:** `WebResourceResponseReceived` on each tab. Read only the URL, method, status, `Content-Type`, `Content-Length`/`Content-Range`, and `Accept-Ranges`. Don't read bodies here.
2. **Classify** (Core, pure function, unit-tested):
   - Direct media: `video/mp4`, `video/webm`, `audio/mp4`, `audio/webm`, `video/quicktime`, …, or an extension fallback when the content type is `application/octet-stream`.
   - HLS manifest: `application/vnd.apple.mpegurl`, `application/x-mpegurl`, `audio/mpegurl`, or `.m3u8`.
   - DASH manifest: `application/dash+xml` or `.mpd`.
   - **Ignore** segments: `video/mp2t`, `.ts`, `.m4s`, `.aac`/`.vtt` chunks, init segments, and anything whose URL appears inside an already-parsed manifest.
3. **Dedupe:** key = scheme + host + path (+ manifest identity). Range requests (`206`) for the same URL merge into one candidate. `Content-Range: bytes 0-…/TOTAL` gives the true size.
4. **Is it a complete file?** A `video/mp4` response may be an fMP4 **segment** of an adaptive stream. Mark a direct candidate as *confirmed* only if (a) the total size is plausible (above a threshold) and it isn't referenced by a manifest, or (b) an injected-script hint reports `<video>.currentSrc` equal to that URL. Otherwise show it under "Other media (unverified)" or hide it.
5. **Manifests:** fetch the body (small) using `Response.GetContentAsync()` if available, else re-fetch with the scoped `RequestContext`. Parse on a background thread. HLS master → variants (`RESOLUTION`, `BANDWIDTH`, `CODECS`, `FRAME-RATE`) plus `EXT-X-MEDIA` audio groups. DASH → `AdaptationSet`/`Representation` (`width`, `height`, `bandwidth`, `codecs`, `mimeType`).
6. **Quality labels come only from data:** manifest attributes, `<video>.videoWidth/Height` when `currentSrc` matches, or FFprobe after download. If unknown, show "Unknown quality". Size is shown when known (`Content-Length`). For HLS/DASH the estimate is `bandwidth × duration`, always labelled "≈ est.".
7. **Scope:** candidates belong to `(TabId, NavigationId)`. A new top-level document clears the list. Same-document (SPA) navigations keep it, but new items are flagged. Closing a tab disposes its store.
8. **UI throttling:** the badge count updates at most every 500 ms. Only top-level items (one per video) are shown, with variants nested underneath.
9. **Optional page hint script** (Phase 5): enumerates `<video>` elements and reports `currentSrc`, dimensions, duration and whether `src` is `blob:`. It's untrusted and used only to enrich labels.

### 6.3 Why `blob:` URLs aren't downloadable files
Modern players use **Media Source Extensions (MSE)**: page JavaScript downloads manifest and segment data with `fetch`/XHR, then appends bytes into a `MediaSource` object. `video.src` is set to `blob:https://site/uuid`, a handle to an object **in that page's memory**, valid only inside that document. There's no file on a server behind it, so it can't be fetched from outside the page, and it may represent only a buffered window of the stream. AJBrowser therefore ignores the blob URL and detects the **real network requests** (manifests and media) that fed it.

### 6.4 FFmpeg usage and containers
- **Remux** (`-c copy`, no re-encoding: fast, lossless) is possible when the codecs are allowed in the output container:
  - H.264/H.265 video + AAC/MP3 audio → **MP4**. HLS MPEG-TS with ADTS AAC needs the `aac_adtstoasc` bitstream filter; recent FFmpeg applies it automatically for MP4 output, but we'll pass it explicitly to be safe.
  - VP8/VP9/AV1 + Opus/Vorbis → **WebM** (or MKV).
  - Anything else or mixed (for example, VP9 + AAC, or subtitles we keep) → **MKV**, which accepts nearly everything.
  - MP4 isn't a good target for Vorbis, some subtitle formats, or certain legacy codecs. `ContainerSelector` picks the best container from parsed codec strings and falls back to MKV, telling the user why.
- **Re-encoding** is never done automatically (slow, lossy, and would require GPL codecs for H.264 encoding). It may become an explicit optional "Convert" feature later.
- Progress comes from `-progress pipe:1` parsing. Cancellation kills the process tree and deletes temp files.

### 6.5 Ad and tracker blocking options

| Option | Quality | Integration (toggle, counter, allowlist) | Effort / maintenance | License |
|---|---|---|---|---|
| **A. WebView2 tracking prevention** (`PreferredTrackingPreventionLevel = Strict`) | Trackers only, not ads | No counter; per-profile only | Trivial | N/A (built-in) |
| **B. Load an MV3 blocker extension** (for example, uBlock Origin Lite) via `AreBrowserExtensionsEnabled` + `AddBrowserExtensionAsync` | High (maintained lists, cosmetic, scriptlets) | **Poor**: extension popup UI isn't available in WebView2, so our toggles and counter would need workarounds | Low to medium. Depends on WebView2 extension support, which is **unverified** for this extension. | uBO Lite is GPLv3 (shipping it unmodified alongside is aggregation, but source must be offered) |
| **C. adblock-rust via a native shim** (our ~150-line Rust `cdylib` exposing create/load/match/cosmetic over a C ABI, called by P/Invoke) | High. Brave's production engine, EasyList/uBO syntax, cosmetic selectors, fast. | **Full**: we call it from `WebResourceRequested` | Medium. Needs a Rust toolchain or prebuilt binaries per arch, and FFI upkeep on engine upgrades. **No existing .NET binding found.** | MPL-2.0 (file-level copyleft; publish any modified engine files) |
| **D. Our own C# ABP-syntax engine** | Medium at best (subset of syntax, no scriptlets) | Full | High and ongoing. Filter syntax is large and evolves. | Our choice |
| ❌ "URL contains 'ad'" | Unacceptable | — | — | — |

**Decision (2026-11-26, after F15):** A from Phase 1, **B (uBO Lite) in Phase 2.5**, and **C (adblock-rust + scriptlet injection) as an optional Phase 8 upgrade** if B's limits (no counter, rule updates only with releases, weak integration) start to matter. D is a fallback only.

**Filter lists:** EasyList + EasyPrivacy (dual-licensed GPLv3 / CC BY-SA 3.0), optionally uBlock filters (GPLv3) and Peter Lowe's list. **Download at runtime** from the official URLs rather than bundling, which avoids redistributing them and keeps them fresh. Honour `! Expires:`, use ETag/If-Modified-Since, cache raw lists plus a **serialised compiled engine** for fast startup, and keep the last good copy on failure. Show list attribution in Settings → About.

**Performance budget:** `WebResourceRequested` runs on the UI thread, so matching must average **well under 0.1 ms** and the handler must never block (no I/O, no locks held long). The spike measures this on heavy news sites.

**Interception coverage:** register the filter with request source kinds (`Document | SharedWorker | ServiceWorker`) using the newer three-parameter `AddWebResourceRequestedFilter` (ICoreWebView2_22). Register worker kinds on only **one** WebView per environment to avoid duplicates. WebSockets and some browser-internal requests aren't interceptable.

**Cosmetic filtering:** on each document and frame (`FrameCreated`, `CoreWebView2Frame`), inject a stylesheet of domain-specific hide selectors. For generic selectors, a small script collects class/id names (with a `MutationObserver`) and asks the engine for matching selectors, which is how adblock-rust is designed to be used. Collapse leftover space with `display: none !important`.

---

## 7. Phased roadmap

Complexity: **S** = a session or two, **M** = several sessions, **L** = many sessions or multiple sub-phases.

---

### Phase 0 — Project verification and foundation · **S**

**Goal / outcome:** The solution has the agreed structure, builds cleanly, and has a test project. The app still opens a window, now titled "AJBrowser". No browser yet.

**Scope:** project structure, packages, composition root, paths, logging, theme choice.
**Excluded:** any browser behaviour.
**Depends on:** none.

**Checklist**
- [x] Create `AJBrowser.Core` (net10.0 class library) and `AJBrowser.Core.Tests` (xUnit). Add both to `AJBrowser.slnx`, and reference Core from the app and the tests.
- [x] Add `CommunityToolkit.Mvvm` to the app (8.4.2).
- [x] Add a `Directory.Build.props` (shared `Nullable`, `ImplicitUsings`, product/version). `TreatWarningsAsErrors` is set in the Core csproj only.
- [x] ~~Optional: add `global.json`~~. **Skipped:** only the .NET 11 SDK is installed, and it builds `net10.0` fine; pinning a .NET 10 SDK would break the build on this machine. Revisit for CI.
- [x] Replace `StartupUri` with `App.OnStartup` that builds `AppServices` and shows `MainWindow` (moved to `Views/MainWindow.xaml`).
- [x] Add a global exception handler (`DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`, `AppDomain.UnhandledException`) that logs (redacted) and shows a friendly dialog.
- [x] `AppPaths` (Core) creates the folders from §5.6.
- [x] `ILogSink` + `AppLogger` (always redacts) + `FileLogSink` (daily files, 5 MB roll-over, keeps 10) + `Redactor` (Core), with tests.
- [x] Theme: `ThemeService` sets `Application.ThemeMode` (WPF0001 suppressed in that one place) and swaps `Themes/Light.xaml` / `Themes/Dark.xaml` semantic brushes. It follows the Windows app mode live when set to System. `Themes/Shared.xaml` holds fonts and sizes. **Your visual check is still pending (V9).**
- [x] Window title "AJBrowser", 1280×800 default, 640×400 minimum, centred. *App icon deferred to Phase 9 (no artwork yet).*
- [x] Add a `README.md` with build/run instructions.

**Implementation notes (2026-11-26):** the agent built the solution with 0 warnings and 0 errors, ran 41 passing unit tests, and launched the Debug exe briefly. The window titled "AJBrowser" appeared, `%LOCALAPPDATA%\AJBrowser\` and its subfolders were created, and the startup log line was written. Not yet checked by a person: theme switching visuals, the test-exception dialog, and Visual Studio F5/Test Explorer. `coverlet.collector` was removed from the test template (not needed).

**Files:** `AJBrowser.slnx`, `AJBrowser/App.xaml(.cs)`, `AJBrowser/MainWindow.xaml`, new `AJBrowser.Core/*`, `AJBrowser.Core.Tests/*`, `Directory.Build.props`, `README.md`.
**Packages:** `CommunityToolkit.Mvvm` (MVVM source generators), `xunit` + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk` (tests).
**Risks:** .NET 11 SDK building net10.0 is fine, but templates may default to net11.0, so set TFMs explicitly. The Fluent ThemeMode is experimental.
**Manual verification:** build the solution (Ctrl+Shift+B). Run (F5): a window titled "AJBrowser" opens. Test Explorer shows passing tests. `%LOCALAPPDATA%\AJBrowser\` exists with subfolders. Throwing a test exception from a debug button produces the dialog and a log line.
**Automated tests:** `Redactor`, `AppPaths` (using a temp root).
**Done when:** the solution builds with no warnings, tests pass, the app starts through the composition root, and folders and logging work.

---

### Phase 0.5 — Feasibility spike: WebView2 interception, downloads, blocking · **S–M**

**Goal / outcome:** Measured answers to the riskiest questions **before** we commit to the media and ad-block architecture. Results are recorded in §11.

**Scope:** a throwaway **DEBUG-only "Diagnostics" window** (or a `spike/` branch) with one WebView2 and a log pane. **Excluded:** production UI. Spike code may be deleted afterwards.
**Depends on:** Phase 0.

**How to run it:** in a Debug build, click **Diagnostics** in the main window. Or start `AJBrowser.exe --diagnostics` (interactive), `--diagnostics-auto` (all automated tests, writes a report and exits), or `--diagnostics-auto=media,popup,download,blocking` (a subset). Reports go to `%LOCALAPPDATA%\AJBrowser\Diagnostics\Reports\`. The spike uses its own WebView2 profile folder (`Diagnostics\WebView2`).

**Checklist**
- [x] Host a WebView2 using an explicit environment (separate spike user data folder under `AppPaths.Root\Diagnostics`). Log `CoreWebView2Environment.BrowserVersionString`.
- [x] **Media responses:** log `WebResourceResponseReceived` while playing a direct MP4, Apple's HLS fMP4 sample, Mux's HLS TS sample, and an Akamai DASH stream → §11 F1, F4. *Redirect reporting still unobserved (no 3xx in tests); re-check in Phase 5.*
- [x] **Manifest body:** `GetContentAsync()` returned every manifest body → §11 F2.
- [x] **Cookies (manual, you):** signed in to GitHub in the Diagnostics window → 18 cookies, 12 HttpOnly → §11 F13.
- [x] **Downloads:** `DownloadStarting` with `Handled` + custom path, pause/resume, and closing the WebView mid-download → §11 F5.
- [x] **Blocking:** three-argument filter (all source kinds), test blocklist, timing and UI-lag on news sites → §11 F6, F7. *Optional manual check: tick "Intercept all requests" and scroll/type on a heavy site.*
- [x] **Tracking prevention:** Balanced vs. Strict request counts → §11 F8.
- [x] **Extension path (manual, you):** loaded uBlock Origin Lite (unpacked) in the Diagnostics window and compared YouTube with the extension on and off → §11 F15. *Measure reload request counts were not recorded (optional).*
- [x] **New windows:** `NewWindowRequested` + deferral + `args.NewWindow` keeps `window.opener` → §11 F10.
- [ ] Optional: minimal adblock-rust C-ABI shim. **Blocked:** no Rust toolchain installed (`cargo` not found). Only needed if option C is chosen.
- [x] Record findings in §11.
- [x] Make the ad-block engine decision in §12: uBO Lite now (new Phase 2.5), adblock-rust later if needed.

**Files:** `AJBrowser/Diagnostics/*` (DEBUG only) or a separate branch.
**Packages:** none new (an optional Rust toolchain for the shim test).
**Risks:** results vary by runtime version, so note the runtime version for each finding.
**Manual verification:** you run the Diagnostics window, follow the scripted steps above, and check the log pane.
**Done when:** every question in §10 marked "spike" has an answer in §11, and the ad-block approach is chosen.

---

### Phase 1 — Working single-tab browser · **M**

**Goal / outcome:** A usable one-tab browser: type a URL or search, navigate, see the title and loading state, and handle errors gracefully.

**Scope:** address bar, back/forward/reload/stop/home, title, loading indicator, error states, runtime-missing handling, tracking prevention baseline.
**Excluded:** tabs, persistence (homepage and search engine are hard-coded defaults for now), downloads UI.
**Depends on:** Phase 0 (and spike learnings).

**Checklist**
- [x] `BrowserEnvironment` creates the shared environment once (lazily, when the first tab starts), with the user data folder from `AppPaths` and profile "Default". `RecreateAsync` replaces it after a browser-process crash, shared by all tabs.
- [x] Runtime check: if `GetAvailableBrowserVersionString` fails, show a dialog with the official WebView2 runtime download link and exit gracefully.
- [x] `BrowserTab` wraps the WebView2 control: `EnsureCoreWebView2Async(environment, controllerOptions)`, and exposes `Navigate`, `GoBack`, `GoForward`, `Reload`, `Stop`. *Simplified events:* one `StateChanged` (URL, title, loading, back/forward are properties), plus `NavigationStarted` and `ProblemOccurred`.
- [x] `UrlOrSearchResolver` (Core): handles a scheme present, bare domain (`example.com`), `localhost:port`, IP addresses, IDN, `?` forced search, and search text → search URL. `javascript:`/`vbscript:` blocked. About 60 test cases, plus 11 for `UrlDisplay`.
- [x] `TabViewModel` + `MainWindowViewModel` with toolbar commands, and `CanExecute` tied to `CanGoBack`/`CanGoForward`.
- [x] Toolbar UI: Back, Forward, Reload/Stop (toggle), Home, address bar (selects all on focus; shows the full URL while editing and the clean URL otherwise via `UrlDisplay`; Enter navigates; Esc reverts). The theme picker and debug buttons moved into a "⋯" menu.
- [x] Loading indicator (thin indeterminate bar along the bottom of the toolbar).
- [x] Errors: `NavigationCompleted.IsSuccess == false` → show a friendly in-view message (hide the WebView to avoid airspace issues) with Retry, mapped from `WebErrorStatus`. Keep WebView2's own cert-error page. **Exception (spike F5):** a navigation that turns into a download completes with `ConnectionAborted`; don't show an error for it. *Implemented as an allowlist of network-level statuses, and only when no HTTP response arrived (a site's own 404 page is shown as-is); see `NavigationErrors`.*
- [x] All per-WebView setup (event handlers, settings, scripts, filters) lives in one `BrowserTab` initialisation method, because new-window WebViews don't inherit any of it (spike F10).
- [x] `ProcessFailed`: renderer crash or hang → "This page crashed / isn't responding — Reload". Browser process exit → recreate the environment and WebView automatically, then reload; a second exit within 30 s shows "The web engine stopped — Try again" instead of looping.
- [x] Set `PreferredTrackingPreventionLevel = Balanced` (Strict as a later setting).
- [x] Set `PreferredColorScheme` to follow the app theme (also `DefaultBackgroundColor`, to avoid a white flash in dark mode).
- [x] DevTools disabled **only in Release** (kept in Debug). Default context menu kept: it provides copy/paste and other basics.
- [x] *Interim (until Phase 2 tabs):* `NewWindowRequested` is always handled by us. A user-clicked `target=_blank` link loads in the same page; script popups are blocked. *Replaced in Phase 2 by real popup tabs.*

**Implementation notes (2026-11-26):** the agent built the solution (0 warnings, 0 errors), ran 113 passing tests (41 old + 72 new), and launched the Debug exe: the window showed the new toolbar, loaded the Bing home page (title "Search - Microsoft Bing - AJBrowser", address bar "www.bing.com"), and closed cleanly. The log showed the runtime check and environment creation. **Not yet checked by a person:** typing in the address bar, search, Back/Forward, Reload/Stop, the error panel, crash recovery, theme switching of pages. See the manual verification list below.

**Files:** `Browser/BrowserEnvironment.cs`, `Browser/BrowserTab.cs`, `Browser/PageProblem.cs`, `ViewModels/MainWindowViewModel.cs`, `ViewModels/TabViewModel.cs`, `Views/MainWindow.xaml`, `Views/Controls/AddressBar.xaml`, `Core/Navigation/UrlOrSearchResolver.cs`, `Core/Navigation/UrlDisplay.cs`, `Core/Navigation/BrowserDefaults.cs`.
**Packages:** none new.
**Risks / edge cases:** `EnsureCoreWebView2Async` must finish before calling `CoreWebView2`. Guard calls and queue the initial navigation. Inputs to consider: `about:blank`, `file://` (allow, or block?), `javascript:` in the address bar (block), IDN domains, and input like `c#` (search, not a URL).
**Manual verification:** F5, type `example.com` → it loads, the title shows. Type `best wpf tutorials` → search results. Click links, then Back/Forward. Reload, and Stop during a slow load. Type `http://nonexistent.invalid` → friendly error + Retry. Disconnect Wi-Fi → error state. Open `chrome://crash` in Debug if allowed (or kill the msedgewebview2 renderer in Task Manager) → crash UI.
**Automated tests:** `UrlOrSearchResolver` (about 30 cases).
**Done when:** everyday single-page browsing works, errors never leave a blank or frozen window, and the resolver tests pass.

---

### Phase 2 — Tabs and everyday browser behaviour · **L**

**Goal / outcome:** Multiple tabs with favicons, new-window handling, reopening closed tabs, keyboard shortcuts, find, zoom, fullscreen, context menu basics.

**Depends on:** Phase 1.

**Checklist**
- [x] `ObservableCollection<TabViewModel>` + `SelectedTab`. Each tab owns its `BrowserTab`.
- [x] Content host: all tab WebViews live in one `Grid` (an `ItemsControl` with a `Grid` panel), and only the selected one is `Visible` (preserves page state). The WebView is disposed on close.
- [x] Tab strip control: favicon, title (ellipsis, tooltip), close button, loading spinner, middle-click to close, "+" button. Overflow: tabs shrink from 240 to 60 px (`ShrinkingTabPanel`); beyond that they're clipped but still reachable with Ctrl+Tab / Ctrl+1…9. Drag-to-reorder is deferred to Phase 9.
- [x] Favicons via `FaviconChanged` → `GetFaviconAsync(PNG)` → `BitmapImage` (frozen), with a globe fallback icon.
- [x] `NewWindowRequested`: take a deferral, create a tab next to the opener, `args.NewWindow = newTab.CoreWebView2`, complete. Respect `args.IsUserInitiated` (block non-user popups with an indicator). **WebView2 does not block them for us** (spike F10). Run the same `BrowserTab` initialisation on the new WebView before assigning `NewWindow`. Popup windows with `WindowFeatures` may open as tabs for now. *Implemented with `PopupRequest`; blocked popups show a toolbar icon whose menu opens them as normal tabs (without `window.opener`). `window.close()` closes the tab.*
- [x] Closed-tab stack (URL, title, index; last 25). `Ctrl+Shift+T` reopens at the old position.
- [x] Keyboard shortcuts: `Ctrl+T`, `Ctrl+W`, `Ctrl+Shift+T`, `Ctrl+Tab`/`Ctrl+Shift+Tab`, `Ctrl+1…8`, `Ctrl+9` (last), `Ctrl+L`/`Alt+D`/`F6` (focus address bar), `Alt+←/→`, `F5`/`Ctrl+R`, `Esc` (stop), `Ctrl+F`, `Ctrl+±/0` (zoom), `F11`, `Ctrl+P`. Shortcuts pressed while the WebView2 has focus arrive through the WPF control's key events / `AcceleratorKeyPressed`. **Verify which keys WebView2 consumes** (§10). *Implemented in `MainWindow.Window_PreviewKeyDown` (also Ctrl+PageUp/PageDown, Ctrl+F4, Alt+Home, F3/Shift+F3, browser media keys). V7 still needs your manual check.*
- [x] Find in page: WebView2's Find API (available, V8) with WebView2's own find dialog suppressed and **our own find bar** docked above the page (match counter, previous/next, Enter/Shift+Enter/Esc, F3).
- [x] Zoom per tab (`ZoomFactor`) with Chrome's zoom steps (`ZoomLevels`), a "125%" indicator that resets on click, and ⋯ menu items. Persisting it per site is deferred.
- [x] HTML5 fullscreen: `ContainsFullScreenElementChanged` → hide chrome, `WindowState=Maximized`, `WindowStyle=None`, and restore on exit. F11 toggles app fullscreen.
- [x] Context menu (`ContextMenuRequested`): "Open link in new tab" (background tab) replaces WebView2's "Open link in new window". Defaults kept otherwise.
- [x] Permission requests (`PermissionRequested`): the default WebView2 prompt is used (no code needed); a custom prompt and per-site memory come later.
- [x] Status text on link hover: **kept WebView2's built-in status bubble** (`IsStatusBarEnabled`), which already draws bottom-left inside the page. No custom `Popup` needed (§12).
- [x] Last tab closed → close the window (configurable later).
- [x] Open `.html`/URLs passed on the command line (`CommandLineUrls`; options starting with "-" are ignored). Later default-browser registration, see §9.

**Implementation notes (2026-11-26):** the agent built the solution (0 warnings, 0 errors) and ran 166 passing tests (53 new: tab selection, closed-tab stack, zoom steps, find counter, command-line URLs). It launched the Debug exe with a local test page and `example.com` as arguments: two tabs opened, the page's script popup was **blocked** (log line + toolbar indicator), the fallback globe icons and titles showed, and the app exited cleanly. One early launch showed a single Bing tab instead; it didn't happen again in 4 further launches (most likely keystrokes reached the window while the test script had brought it to the front). **Fixed along the way:** the WebView's default background is now always white; the dark colour from Phase 1 made pages without their own background unreadable (black text on dark). **Not yet checked by a person:** clicking popups and `target=_blank` links, sign-in popups, middle-click, shortcuts while the page has focus (V7), find counter numbering (V8), fullscreen video, zoom, print, context menu.

**Files:** `Views/Controls/TabStrip.xaml`, `Views/Controls/ShrinkingTabPanel.cs`, `Views/Controls/FindBar.xaml`, `Views/MainWindow.xaml(.cs)` (shortcuts, fullscreen), `ViewModels/MainWindowViewModel.cs`, `ViewModels/TabViewModel.cs`, `Browser/BrowserTab.cs`, `Browser/PopupRequest.cs`, `Core/Tabs/*`, `Core/Navigation/{ZoomLevels,FindStatus,CommandLineUrls}.cs`.
**Packages:** none.
**Risks / edge cases:** memory with many tabs (suspend background tabs later); focus juggling between WPF and the WebView2 HWND; disposal races (events firing after close, so unsubscribe and null-check); `NewWindowRequested` must assign `NewWindow` before navigation starts; popups that call `window.close()` (`WindowCloseRequested` → close the tab).
**Manual verification:** open 10 tabs and switch with the mouse and `Ctrl+Tab`. Close the middle tab → the correct neighbour becomes active. `Ctrl+Shift+T` restores it. Middle-click a link → new tab. "Sign in with Google" on a site → the popup works and closes itself. YouTube video fullscreen works. `Ctrl+L` while the page has focus focuses the address bar. Zoom in/out/reset.
**Automated tests:** closed-tab stack logic and tab-selection-after-close logic (pure functions in Core or testable VM logic).
**Done when:** daily multi-tab browsing feels normal, with no leaked WebView processes after closing tabs (check Task Manager).

---

### Phase 2.5 — Ad blocking with uBlock Origin Lite · **S–M**

**Goal / outcome:** Ads (including YouTube video ads) are blocked in the normal browser by default, using the uBlock Origin Lite extension proven in the spike (F15), with a simple on/off control.

**Scope:** load and manage the bundled extension; global on/off; licensing notice. **Excluded:** blocked-count badge, per-site toggles and background list updates (these come with the Phase 8 adblock-rust upgrade, if we do it).
**Depends on:** Phase 2 (popups and all WebViews go through the shared `BrowserTab` setup). Decided 2026-11-26 (§12).

**Checklist**
- [x] `BrowserEnvironment`: set `AreBrowserExtensionsEnabled = true` in the environment options (applies to every WebView in the user data folder).
- [x] Bundle uBO Lite in the app output, pinned to a version, with its `LICENSE` (GPLv3) and a source link, and list it in `THIRD-PARTY-NOTICES.md`. *As built:* we ship the **official release zip, unmodified** (`AJBrowser/Extensions/uBOLite_2026.1124.1811.chromium.zip`, 9 MB, SHA-256 in the notices file) instead of ~1,000 unpacked files. Its `LICENSE.txt` and build `README.md` are inside the zip.
- [x] Unpack it from the install folder to `%LOCALAPPDATA%\AJBrowser\Extensions\uBOLite\<version>\` before installing, because WebView2 references the folder in place (F15) and the install folder may change on app updates. Extraction goes to `<version>.partial` and is renamed at the end, so an interrupted unpack is never used.
- [x] `ExtensionManager` (Browser layer): on startup, `GetBrowserExtensionsAsync`; install with `AddBrowserExtensionAsync` if missing; when the bundled version is newer (or the version is unknown, or its folder is gone), remove the old one and install the new one; delete old version folders. Log the outcome. WebView2 doesn't report extension versions, so `Extensions\uBOLite\state.json` records id, version, folder and the on/off choice. Every new WebView calls `PrepareAsync` before its first navigation; only the first WebViews wait (at most 20 s), so the first page is already ad-free.
- [x] On/off: a **toolbar shield** next to ⋯ shows the state at a glance (green filled shield with a tick = on; grey shield with a slash = off or unavailable). Clicking it opens a panel with a **Block ads** switch (`SwitchToggleStyle`, a restyled ToggleButton), a status line and the uBO Lite version. The switch calls `extension.EnableAsync(bool)` and reloads the current page. The choice is saved in `state.json` and applied again at every start (moves to settings in Phase 3). *First built as a ⋯ menu item; replaced after user feedback because the Fluent theme didn't draw its tick, so the state wasn't visible.*
- [x] The panel's **Settings** link opens `chrome-extension://<id>/dashboard.html` in a new tab (filtering mode, per-site exceptions).
- [x] Failure handling: if extensions can't load (old runtime, policy, missing zip), browsing continues without blocking, the log says why, the shield is grey and its panel says "Ad blocker unavailable" with the switch disabled; tracking prevention stays on.

**Implementation notes (2026-11-26):** the agent built the solution (0 warnings, 0 errors) and ran 205 passing tests (39 new: extension versions, install decision, zip reading/unpacking/cleanup, state file). It launched the Debug exe three times against the real profile: first start **installed** uBO Lite in about 4 s (`Installed uBlock Origin Lite 2026.1124.1811 (id jhflpejankfpjpflehfmfgckodndmdjc)`); the second start **reused** it (ready 0.6 s after the environment); with `Enabled: false` in `state.json` the next start reported `off`, and switching it back reported `on`. **User check (2026-11-26):** ad blocking works in the normal browser. **Shield follow-up:** the agent drove the shield and switch through UI Automation: on/off screenshots looked right, `state.json` and the log followed each switch (`Ad blocking turned off` / `on`). Not yet checked by a person: the settings link, the light theme look.
**Known limits:** a new uBO Lite version installs from a new folder, so it gets a **new extension id** and the user's uBO Lite dashboard settings (filtering mode, per-site exceptions) reset on upgrade; our own on/off choice is kept. Pages already open keep their current state until reloaded (only the current tab reloads on toggle). **Open issue F16:** YouTube repeatedly shows "Experiencing interruptions?" while videos load, most likely because of uBO Lite; deferred to Phase 8 (§11).

**Files:** `Browser/BrowserEnvironment.cs`, `Browser/ExtensionManager.cs`, `Browser/BrowserTab.cs`, `AppServices.cs`, `ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.xaml`, `AJBrowser.csproj` (content items), `Extensions/uBOLite_*.chromium.zip`, `THIRD-PARTY-NOTICES.md`; Core `AdBlocking/` (`ExtensionVersion`, `ExtensionPackage`, `AdBlockerInstallPlanner`, `AdBlockerState`), `AppPaths.Extensions`.
**Packages:** none (the extension is the bundled release zip, GPLv3, shipped unmodified alongside the app; unzipping uses the in-box `System.IO.Compression`).
**Risks:** rules only update with new uBO Lite versions (F15), so plan to bump the bundled version in releases; YouTube may break blocking until uBO Lite updates; WebView2 extension support is newer than the rest of the API surface.
**Manual verification:** fresh profile → F5 → the extension installs itself (log line). YouTube: no pre-roll ads on 3–4 videos. Turn blocking off → ads return; on → gone; restart → the choice is kept. A news site visibly loses ad slots.
**Automated tests:** version comparison / "needs reinstall" decision as a pure function in Core.
**Done when:** ads are blocked by default in the normal browser without any manual extension loading, and the user can turn it off.

---

### Phase 3 — Bookmarks, history, settings and persistence · **M–L**

**Goal / outcome:** Bookmarks, browsing history with search, settings (homepage, search engine, theme, download folder), and session restoration.

**Depends on:** Phase 2.

**Checklist**
- [x] Add `Microsoft.Data.Sqlite` (11.0.0) to Core. `Database` opens with WAL mode, a 5 s busy timeout and foreign keys on; migrations via `PRAGMA user_version`, one transaction each. A corrupt file is moved aside (`ajbrowser.db.corrupt-<time>`), recreated, and the user is told at startup.
- [x] Schema v1: `history(id, url UNIQUE, title, visited_at, visit_count)`, `bookmarks(id, parent_id → cascade, title, url NULL = folder, position, created_at)`, `session_tabs(window_id, position, url, title, is_active)`. *Changed:* `site_settings` and `downloads` were left out of v1; they get their own migrations in the phases that use them (Phase 8 / Phase 4), because forward-only migrations make adding tables later trivial (§12).
- [x] Repositories (`HistoryRepository`, `BookmarkRepository`, `SessionRepository`) with async methods that run their SQL via `Task.Run`, so the UI thread never touches the DB. App-side services: `HistoryService` (queued writes, so a title update can't overtake its visit), `BookmarkService` (all bookmarks in memory, write-through), `SessionService` (debounced saves).
- [x] `SettingsStore` (`settings.json`, System.Text.Json, temp + replace, readable enum names, missing fields get defaults, invalid values normalised, an unreadable file is kept as `settings.json.bad`): home page, startup (home page / restore session), search engine (Bing, Google, DuckDuckGo, Brave Search, custom `{query}` template), theme, bookmarks bar, download folder, ask-where-to-save. *Changed:* the ad-block on/off stays in the extension's `state.json` (one source of truth); the Settings page shows the same switch as the shield (§12).
- [x] History recording on successful top-level `NavigationCompleted` **and** same-document URL changes (`SourceChanged` with `IsNewDocument = false`, so YouTube/Gmail-style apps are recorded). http(s) only, `#fragment` dropped, URLs over 8 KB skipped, titles cut at 1,000 chars. Revisits within 30 s don't add to the count. The late page title updates the entry. (No private windows yet.)
- [x] History page: search (debounced), grouped by day (Today / Yesterday / date), 200 at a time with "Load more", delete entry, "Clear browsing data" for a time range with an optional "also clear cookies, site data and cache" (`Profile.ClearBrowsingDataAsync` with `AllSite | DiskCache | BrowsingHistory`; not passwords, autofill or settings). Ctrl+H; Ctrl+Shift+Delete opens it with the clear panel.
- [x] Bookmarks: ☆ star in the address bar (`Ctrl+D`) adds to the bookmarks bar and opens an edit panel (name, folder, Remove; saved on close); Bookmarks page (Ctrl+Shift+O) with folders, search, a details panel (rename, change address, move, delete), New folder, Enter/double-click to open, middle-click for a background tab; bookmarks bar (on by default, Ctrl+Shift+B) with folder drop-down menus. Reordering is not supported yet (§9).
- [x] Address bar suggestions from history and bookmarks: 120 ms debounce, background query (50 candidates), ranked in Core (`SuggestionRanker`: address prefix > host part > title word > contains; bonuses for bookmarks, visits, recency), top 6, ↑/↓ + Enter or click, Esc closes the list first.
- [x] Internal pages as **WPF views shown in a tab** with `ajbrowser://history|bookmarks|settings` addresses (typeable, saved in the session, never sent to WebView2). A tab shows its web page or an internal page; the web engine starts only when needed. Back on an internal page returns to the web page behind it. The Downloads page comes with Phase 4.
- [x] Session restore: tab list saved 1 s after changes and synchronously at exit; restored on startup when "Continue where you left off" is chosen. Background tabs load only when first selected (no WebView until then). Command-line URLs open after the restored tabs.
- [x] Import from other browsers is deferred (§9).
- [x] *Follow-up (your request, 2026-11-26):* **site icons** in History, Bookmarks, the bookmarks bar (and its folder menus) and suggestions. Migration **v2** adds `favicons(host PK, png, updated_at)`; every icon a tab receives is remembered per host (`FaviconService`: all in memory, background writes, max 64 KB per icon) and shown by a small `SiteIcon` control, with the globe/folder/star glyph as fallback. Sites visited before this change get their icon on the next visit. **Fixed at the same time:** switching between two internal-page tabs (History ↔ Bookmarks) kept showing the last-opened page; the visibility binding sat on a `ContentPresenter`, whose DataContext is its content, so it never resolved.
- [x] *Follow-up 2 (your report: blurry, misshapen icons):* WebView2's `GetFaviconAsync` only gives **16 px** icons, which a 125% display stretches to 20 px; the decoder also forced 16→32 px first. Now: icons decode at their real size; they snap to whole pixels (`UseLayoutRounding`); and when a site's stored icon is under 32 px, `FaviconService` downloads the icon file once per site and session (no cookies, ≤ 256 KB, 8 s timeout, the browser's user agent) and keeps its sharpest picture up to 64 px (`FaviconImages.ToBestPng`, ICO/PNG/GIF/JPEG/BMP), falling back to `/favicon.ico`. **SVG icons are skipped** (WPF can't draw SVG; they're often theme-aware: GitHub's white SVG vs. its black favicon.ico), so those sites keep WebView2's correctly coloured 16 px copy. A smaller icon never replaces a bigger one (`FaviconRules`). Tabs show the sharper icon too. Checked: Google 16 → 32 px (crisp and round on tab and in History), GitHub stays the white 16 px icon.

**Implementation notes (2026-11-26):** the agent built the solution (0 warnings, 0 errors) and ran 282 passing tests (77 new: migrations on empty/old/newer/failing/corrupt databases, history/bookmark/session repositories on temp SQLite files, settings round-trip/defaults/invalid values/bad file, search templates, internal addresses, history rules, suggestion ranking). It then drove the Debug app with UI Automation and screenshots: DB created (schema v1); star → bookmark saved, bar updated, edit panel opens under the star; History shows the visit under "Today"; Bookmarks and Settings pages render; typing "exa" in a new tab suggests the bookmark and ↓ highlights it; with "Continue where you left off", closing saved 4 tabs and the next start restored all 4 with only the active one loaded; "Clear browsing data" (last hour, history only) deleted 4 entries. **Fixed along the way:** the star's panel opened at the far left (popup anchors now set in code); typing into a new tab's address bar could get the home page's address mixed in (the new tab now takes over the "editing" state, and the address never changes while the box has focus). The test database and settings were deleted afterwards, so your profile starts clean. **Not yet checked by a person:** everything in "Manual verification" below, the light theme on the new pages, and clearing cookies/site data (not run, to keep your sign-ins).
**Known limits:** keystrokes sent in an instant burst right after Ctrl+T can lose their first characters (normal typing speed is fine; pre-existing focus timing from Phase 2). Closing the last tab closes the window and saves an empty session, so the next start shows the home page. History keeps one row per URL, so a page revisited today no longer appears under older days. Whether "also clear cookies, site data" touches the ad blocker's own storage is unverified.

**Files:** Core `Persistence/` (`Database`, `Migrations`, `Sql`, `HistoryRepository`, `BookmarkRepository`, `SessionRepository`), `Settings/` (`AppSettings`, `SettingsStore`, `SearchEngines`), `Navigation/` (`InternalPages`, `HistoryRules`, `SuggestionRanker`; `UrlOrSearchResolver.SearchTemplate`; `BrowserDefaults` removed); App `Services/` (`HistoryService`, `BookmarkService`, `SessionService`), `ViewModels/` (`AdBlockViewModel`, `InternalPageContracts`, `SuggestionsViewModel`, `BookmarkEditorViewModel`, `Pages/*PageViewModel`), `Views/Pages/*Page.xaml`, `Themes/Pages.xaml`, `AddressBar`, `MainWindow`, `TabViewModel`, `MainWindowViewModel`, `AppServices`, `App.xaml(.cs)`, `BrowserTab` (`PageLoaded`), `BrowserEnvironment` (`ClearBrowsingDataAsync`).
**Packages:** `Microsoft.Data.Sqlite` 11.0.0 (local relational storage; brings SQLitePCLRaw with the native e_sqlite3 library).
**Risks / edge cases:** DB locked/corrupt → back up and recreate with a user notice; very long URLs/titles; migrations must be forward-only and tested; restore must not open hundreds of tabs eagerly (lazy-load background tabs).
**Manual verification:** visit pages → they appear in History, searchable. Bookmark a page and restart → the bookmark persists. Change the search engine → the address bar uses it. Restart with "restore session" → tabs return. Clear history works.
**Automated tests:** migrations on an empty and an old-version DB (temp file), repository CRUD (in-memory or temp SQLite), settings serialisation round-trip and defaults for missing fields, suggestion ranking.
**Done when:** data survives restarts, the UI never blocks on DB access, and the settings take effect.

---

### Phase 4 — Normal download manager · **L**

**Goal / outcome:** Browser downloads appear in AJBrowser's own download panel and page, with progress, speed, ETA, pause/resume where supported, cancel, retry, open, show in folder, and persistent history.

**Depends on:** Phase 3 (persistence, settings). It also builds the `HttpFileDownloader` that Phase 5 uses.

**Checklist**
- [x] *(4a)* `DownloadItem` model (Core): `Id`, `Kind` (Browser/App), `State` (Queued, Downloading, Paused, Completed, Failed, Cancelled, Interrupted), `SourcePageUrl`, `DisplayUrl` (redacted), `FilePath`, `TotalBytes?`, `ReceivedBytes`, `StartedAt`, `CompletedAt`, `Error`, `CanPause`, `CanResume`.
- [x] *(4b)* `DownloadInterceptor` (Browser): `DownloadStarting` → `Handled = true`. Compute the path via `FileNamer` and settings (ask-where-to-save shows `SaveFileDialog` using a deferral). Subscribe to `BytesReceivedChanged`, `StateChanged`, `EstimatedEndTimeChanged`.
- [x] *(4a)* `DownloadManager` (Core): owns all items, persists state changes, throttles progress (≈4/sec), computes speed (moving average) and ETA (when the total is known). Also tracks browser downloads reported by the Browser layer (`AddBrowserDownloadAsync`, `UpdateBrowserDownload`; wired in 4b). Events are posted to the creating `SynchronizationContext` (the UI thread).
- [x] *(4a)* `HttpFileDownloader` (Core): streaming to `.ajpart`, a buffer of ≥ 81 KB, Range resume when supported (`Accept-Ranges`, `ETag`/`Last-Modified` validation via `If-Range`), retry with backoff on transient errors, cancellation, atomic rename. It has no UI dependency and is used for app-managed jobs.
- [x] *(4a)* Queue and concurrency: app-managed jobs run at most `MaxConcurrent` at a time (default 3, 1–10; a small scheduler instead of `SemaphoreSlim`, so the limit can change at runtime). Browser downloads are not queued by us (the runtime controls them); document this.
- [x] *(4b)* Download panel: a toolbar button with a progress ring badge, and a `Popup` flyout listing recent downloads (it renders above the WebView). "Show all" opens the Downloads page.
- [x] *(4b)* Downloads page: the full list, search, clear completed, per-item actions.
- [x] *(4b)* Actions: Open (`ShellExecute` via `Process.Start` with `UseShellExecute=true`), Show in folder (`explorer.exe /select,"path"` via `ArgumentList`), Copy link (redacted for signed URLs), Retry (browser: navigate to the original URL in a tab; app: restart the job), Cancel (deletes the partial file).
- [x] *(4c)* **Tab close with active browser downloads (spike F5):** closing a WebView freezes its downloads. Keep the owning WebView alive (detached and hidden) until its downloads finish, or ask the user before closing. Never infer progress from the target file's size.
- [x] *(4a/4c)* On startup: mark previously in-progress browser downloads as `Interrupted`, offer resume for app jobs, and clean orphaned `.ajpart` files older than N days (ask first).
- [x] *(4b/4c)* Settings: default folder, always-ask, max concurrent app downloads. *(4b: the folder and always-ask settings are now honoured, and a moved Windows Downloads folder is found via `FOLDERID_Downloads`; the max-concurrent setting comes in 4c.)*
- [x] *(4b)* Dangerous file types: rely on WebView2 SmartScreen for browser downloads (a blocked file shows "Blocked: the file may be harmful"). App downloads only fetch media types.

**Checkpoint 4a (2026-11-26), done:** Core only, no UI change. `Downloads/DownloadItem` (model, `DownloadStateMachine`, `ResumeInfo`), `FileNamer` (sanitising, device names, path segments stripped, Content-Disposition incl. `filename*`, extension from content type, `name (1).ext` that also respects `.ajpart`, in-process reservations), `TransferRateEstimator` (5 s window), `HttpFileDownloader` (`.ajpart` + rename without overwriting, Range/If-Range resume, restart on 200/416/changed ETag, retries 1/2/4 s on network errors, timeouts, 408/429/5xx, stall timeout 30 s, fatal on 4xx and write errors, error messages never contain the URL), `DownloadManager`; `Persistence/DownloadRepository` + migration **v3** `downloads` table (`MarkStaleAsync`: browser downloads that were running → Interrupted, app downloads → Paused). 59 new tests (357 total, stable over 3 runs) incl. a fake HTTP server for resume, ignored range, changed file, dropped connection, 5xx back-off, 404, stall, cancel, no-overwrite. The app was launched once: database upgraded to v3, no errors. **Next: 4b** = `DownloadInterceptor`, toolbar download button + flyout, Downloads page, actions. **Then 4c** = tab close with active downloads (F5), startup cleanup of old `.ajpart` files, settings (max concurrent), app-exit confirmation.

**Checkpoint 4b (2026-11-26), built, awaiting your manual check:** `Browser/DownloadInterceptor` (one per app, attached to every WebView in `BrowserTab.Configure`): hides Edge's flyout, uses WebView2's suggested name made safe by `FileNamer.Sanitize` + `ReservePath` in the Settings folder (created if missing, else the Windows Downloads folder via `Services/KnownFolders`), or a Save dialog through a deferral when "Ask where to save" is on; registers with `DownloadManager` and forwards `StateChanged`/`BytesReceivedChanged`; pause/resume/cancel of browser downloads go to the live operation (gone after a restart → Retry). Core `DownloadText` (sizes, speed, time left, status line) and `BrowserDownloadStatus` (WebView2 state/interrupt-reason names → our states and short messages), 33 new tests (390 total). UI: toolbar download button with a progress ring (`Views/Controls/ProgressArc`, spins when the size is unknown), flyout `Views/Controls/DownloadPanel` (newest 5, opens by itself when a page starts a download, click a finished row to open it, Show all), `ajbrowser://downloads` page (Ctrl+J, ⋯ → Downloads): search, Open folder, Clear finished, per item Open / Show in folder (`SHOpenFolderAndSelectItems`) / Pause / Resume / Cancel / Retry / Copy link / Remove. `DownloadManager` is created in `AppServices`, loaded at startup, and app downloads are paused at exit. Build 0 warnings; **the app was not launched by the agent this time** (launching it would have written into your real `%LOCALAPPDATA%\AJBrowser` data, which was refused), so everything visual is unverified. **Known gap until 4c:** a download from a link that opens a *new tab/popup* freezes, because Chromium closes that popup and closing a WebView freezes its downloads (F5). **Next: 4c** = keep a WebView alive while it owns downloads (also auto-close the blank tab a Retry opens), startup cleanup of old `.ajpart` files, max-concurrent setting, app-exit confirmation with active downloads.

**Checkpoint 4c (2026-11-26), built, awaiting your manual check:** first a standalone WebView2 spike (scratch app with its own data folder, local slow test server): disposing a WebView froze its download (F5 reproduced), while a WebView only taken out of the window kept downloading to completion, also after navigating to about:blank, and pause/resume still worked (§11 F18). Built on that: `DownloadInterceptor` now records which WebView owns each download **as soon as `DownloadStarting` fires** (a popup that only started a download closes itself right after); `BrowserTab.Dispose` hands a WebView that still owns running/paused downloads to `DownloadInterceptor.Park` (muted, navigated to about:blank, out of the window), which disposes it when its last download completes, fails or is cancelled. A tab that never showed a page and only started a download (new-tab download links, Retry, typed download URLs) closes itself (`BrowserTab.OnlyDownloaded`; the last tab goes to the home page instead) and isn't added to Ctrl+Shift+T. Closing the window with unfinished browser downloads asks first (default No; not during Windows shutdown); if the last tab's close is refused, a home tab reappears. Startup offers to delete `.ajpart` files older than 7 days in the download folders that no app download will continue (`Core/Downloads/PartialFileCleanup`). Settings → "Simultaneous video downloads" (1–10, default 3, `AppSettings.MaxConcurrentDownloads`, applied to `DownloadManager.MaxConcurrent` live). 9 new tests (399 total), build 0 warnings. **Then driven by the agent in the real app at your request (2026-11-26, UI Automation + a local test server; your database backed up and restored afterwards, test files deleted):** a `target=_blank` download link → the new tab closed itself within 0.5 s, the 60 MB download completed and the kept engine was released; closing the tab of a running download → it completed 15 s later, engine released; on a closed tab's download the Pause button appeared, Pause held it ("Paused · 20.5 MB of 200 MB") and Resume continued; ✕ with a download running → the question appeared with the right text, No kept the browser open, Yes closed it. Note for automation: while the downloads flyout is open, the first click elsewhere (including ✕) only closes the flyout, as usual for popups. Also fixed after 4b: Pause button missing on running browser downloads, and an O(n) speed calculation (§12).

**Files:** `Core/Downloads/*`, `Browser/DownloadInterceptor.cs`, `ViewModels/DownloadsViewModel.cs`, `DownloadItemViewModel.cs`, `Views/Controls/DownloadPanel.xaml`, `Views/Pages/DownloadsPage.xaml`.
**Packages:** none new.
**Risks / edge cases:** unknown total size (indeterminate progress); a server that ignores Range and returns `200` → restart from zero; `ETag` change → restart; disk full; path too long; the target folder deleted mid-download; same filename racing between two downloads (reserve names atomically); a download from a tab that gets closed (see spike result); app exit with active downloads (confirm dialog).
**Manual verification:** download a large public test file (for example, from a speed-test site) → see the speed and ETA. Pause → resume (where supported). Cancel → the partial file is removed. Download the same file twice → `name (1).ext`. Restart the app mid-download → shown as Interrupted, Retry works. Open and Show-in-folder work.
**Automated tests:** `FileNamer` (sanitising, reserved names, dedupe, long paths); `HttpFileDownloader` using a fake `HttpMessageHandler` (full, ranged resume, server ignores range, ETag mismatch, mid-stream failure + retry, cancellation cleanup); speed/ETA calculator; state-machine transitions.
**Done when:** downloads never freeze the UI, states are always accurate after a restart, and partial files are never mistaken for complete ones.

---

### Phase 5 — Direct video detection and download picker · **M–L**

**Goal / outcome:** Playing a video that's a direct MP4/WebM file shows a **"Videos detected"** button. Clicking it lists the videos (with resolution/size when known), and one click downloads through the download manager.

**Depends on:** Phase 4, plus the spike findings.

**Checkpoints:** **5a** Core only (classifier, per-tab store with de-duplication and confirmation, hint validation, `RequestContext`, `Redactor.MediaUrl`, file names); **5b** Browser wiring and UI (`MediaSniffer`, hint script, "Videos detected" button and picker, download with headers and cookies); **5c** context-menu "Download video", Settings switch, "Save as…", expired-link message, check in the real app via automation.

**Checklist**
- [ ] `MediaSniffer` (Browser): subscribe to `WebResourceResponseReceived` per tab, extract metadata cheaply, and push to a `Channel<MediaObservation>`. Capture the `NavigationId` from `NavigationStarting`.
- [x] *(5a)* `MediaClassifier` (Core): content type first, extension as a fallback, segment exclusion rules (§6.2). Pure and tested. Use the real content types from spike F1 as test cases (`audio/mpegurl` manifests, untyped or `octet-stream` TS segments, DASH `.m4v`/`.m4a` segments served as `video/mp4`/`audio/mp4`, byte-range `main.mp4` behind an fMP4 HLS playlist).
- [x] *(5a)* `Redactor.MediaUrl` (Core): also shortens long or opaque path segments, because CDNs put tokens in the path (spike F12). Use it for every media URL in logs and history.
- [x] *(5a)* Deduplicate live-manifest refreshes (the same manifest is fetched every few seconds; spike F1).
- [ ] `MediaDetectionService` (Core): per-`(TabId, NavigationId)` store, dedupe, confirmation logic, events (throttled). Clear on new document, dispose on tab close. *(5a: the per-tab store is `Core/Media/TabMediaStore` (dedupe, confirmation, reset on a new document, SPA flag, `Version` for throttled UI); one per tab and the throttled events come in 5b.)*
- [x] *(5a)* `RequestContext` (Core/Security): captures the **allowlisted** request headers from the observed request (`Referer`, `Origin`, `User-Agent`, `Accept-Language`). Cookies are fetched **at download start** from the tab's profile `CookieManager.GetCookiesAsync(mediaUrl)` (cookies the browser would send to that exact URL). Never copy `Authorization` from another origin; never persist the context.
- [ ] Optional hint script (`Scripts/video-hints.js`, injected with `AddScriptToExecuteOnDocumentCreatedAsync`): posts `{currentSrc, videoWidth, videoHeight, duration, isBlob}` for `<video>` elements on `loadedmetadata`. The host validates and treats it as untrusted.
- [ ] UI: a toolbar "Videos detected (n)" button, visible only when n > 0. `Popup` picker: each item shows thumbnail-less info (page title, resolution or "Unknown quality", format, size or "Unknown size", source host), a Download button, and a "Save as…" option.
- [ ] Download → `DownloadManager.EnqueueApp(job)` using `HttpFileDownloader` with the `RequestContext`. Suggested filename: page title + resolution + extension.
- [ ] Expired URL handling: a `403`/`410` on start → "Link expired — replay the video and try again" (and re-detection auto-refreshes the URL).
- [ ] Add "Download video" to the context menu when right-clicking a `<video>` with a non-blob `src`.
- [ ] Settings toggle: enable/disable media detection.

**Checkpoint 5a (2026-11-26), done:** Core only, no UI change. `Core/Media/MediaClassifier` (Direct / HLS / DASH / Segment / None from content type, then extension; segment rules for `.ts`/`.m4s`/`.aac`/`.vtt`, `video/mp2t`, `init*.mp4`, `seg-12`/`chunk_3`/`frag5` names; ignores non-200/206, non-http and non-media resource contexts; `TotalSize` from `Content-Range` total or `Content-Length`; `KeyOf` = scheme+host+path), `Core/Media/TabMediaStore` (one entry per file across range requests and token changes, keeps the newest URL and largest size; a direct file is **confirmed** only if a native player or a navigation loaded it (resource context Media/Document) and it's ≥ 1 MB, or the page's player reported it; files fetched by page script (Fetch/XHR) are treated as stream pieces, which covers spike F1's byte-range `main.mp4`; HLS/DASH are listed; `Reset` on a new document, `MarkSpaNavigation` flags later items; max 200 entries; hints only enrich existing entries), `VideoHint.TryParse` (strict JSON, 4 KB cap, `type: "ajb-video"`, http(s) only, blob flagged, dimensions/duration range-checked), `Core/Media/MediaFileNamer` ("Title 720p.mp4"), `Core/Security/RequestContext` (allowlist Referer/Origin/User-Agent/Accept-Language, no CR/LF, 2 KB cap, page origin as fallback Referer, Cookie only added by `ToHeaders` at download time), `Redactor.MediaUrl` (path segments of 40+ chars or 16+ with ≥2 digits and ≥2 letters → `[id]`, extension kept). 73 new tests (472 total). **Next: 5b.**

**Files:** `Browser/MediaSniffer.cs`, `Browser/Scripts/video-hints.js`, `Core/Media/*`, `Core/Security/RequestContext.cs`, `ViewModels/MediaPickerViewModel.cs`, `Views/Controls/VideoPicker.xaml`.
**Packages:** none new.
**Risks / edge cases:** sites splitting one MP4 into many ranged requests (dedupe); a CDN redirect chain (use the final URL, but keep the original page context); identical videos under different query tokens (dedupe by path, keeping the latest URL); preview or thumbnail videos on hover flooding the list (size threshold, ignore muted autoplay snippets, or mark them low priority); cookie scoping across CDN domains (only cookies valid for the CDN URL are sent, which is correct browser behaviour); private tab detections must use the private profile.
**Manual verification:** a local test HTML page with `<video src="sample.mp4">` served by `python -m http.server` → detected, with resolution from the hint. A site with a direct MP4 → detected with its size. An HLS site → nothing yet, or an "HLS — supported soon" label, but **no segment flood**. Navigate to a new page → the list clears. Download → it appears in the download manager and plays in a media player.
**Automated tests:** classifier table tests (content types, extensions, segments); dedupe and confirmation logic; `RequestContext` header allowlist (ensures `Cookie`/`Authorization` are never copied from observed requests); filename suggestion.
**Done when:** direct videos on test pages are reliably detected once (not per range request), the list scopes correctly per tab and navigation, and downloads use the correct cookies without leaking any.

---

### Phase 6 — Unprotected HLS quality selection and downloads · **L**

**Goal / outcome:** For HLS videos, the picker lists the real qualities from the master playlist (for example, 1080p · 5.2 Mbps · H.264/AAC · ≈ 740 MB est.). Choosing one downloads all segments and produces a playable MP4 (or MKV when needed).

**Depends on:** Phase 5.

**Checklist**
- [ ] `HlsParser` (Core): master playlists (`EXT-X-STREAM-INF`, `EXT-X-MEDIA` audio/subtitle groups, `EXT-X-I-FRAME-STREAM-INF` ignored), media playlists (`EXTINF`, `EXT-X-BYTERANGE`, `EXT-X-MAP`, `EXT-X-KEY`, `EXT-X-DISCONTINUITY`, `EXT-X-ENDLIST`, `EXT-X-MEDIA-SEQUENCE`). Relative URI resolution.
- [ ] Classification: `METHOD=AES-128` → supported (clear key fetched over HTTPS from the site, standard HLS, not DRM). `SAMPLE-AES`, `SAMPLE-AES-CTR`, or `KEYFORMAT` = FairPlay/Widevine → **"Protected (DRM) — not supported"**. No `EXT-X-ENDLIST` → "Live — not supported yet".
- [ ] Variant UI: resolution if present, else bandwidth only. Codecs → friendly names. Duration from the sum of `EXTINF` (fetch the chosen media playlist lazily for the size estimate). Separate audio groups listed when present.
- [ ] Segment downloader (Core): parallel (4–6) segment fetch with the same `RequestContext`, per-segment retry, AES-128 decryption (`System.Security.Cryptography.Aes`, CBC, IV from the tag or the media sequence number), byte-range support, temp folder per job, resume by skipping completed segments, progress = segments + bytes.
- [ ] FFmpeg integration: `FfmpegLocator` (bundled path / settings path / on-demand download, see Phase 10), `FfmpegRunner` (ArgumentList, `-progress pipe:1`, cancellation kills the process). Remux from a **local** playlist or concat list, so FFmpeg does no network access and never sees cookies.
- [ ] `ContainerSelector` (Core): codecs → MP4/WebM/MKV with a reason string (§6.4).
- [ ] Output naming and cleanup of the temp folder on success, cancel, or failure (keep on failure if resumable).
- [ ] FFprobe after remux (optional) to confirm duration and resolution and record them.
- [ ] Error UX: FFmpeg missing → offer the download/locate action; key fetch `403` → "Session expired, replay the video".

**Files:** `Core/Media/Hls/*`, `Core/Media/SegmentDownloader.cs`, `Core/Media/Ffmpeg/*`, picker UI updates.
**Packages/tools:** FFmpeg + FFprobe (LGPL build), external executables.
**Risks / edge cases:** huge segment counts (memory, so stream the list); discontinuities (ad breaks, codec changes) can break a remux, so try concat with `-fflags +genpts` or fall back to MKV; fMP4 HLS (`EXT-X-MAP`) vs. TS; separate audio rendition must be downloaded and merged; key rotation per segment; tokens in segment URLs expiring mid-download (refresh the playlist and resume); rate limiting (back off); ADTS audio in MP4 (`aac_adtstoasc`).
**Manual verification:** Apple's public HLS sample streams (TS, fMP4, multi-audio) → the picker shows the exact variants from the master playlist. Download 720p → the result plays in the Windows Media Player / VLC with the correct resolution (check properties). Cancel mid-way → temp files cleaned. Pull the network mid-download → retry/resume works.
**Automated tests:** `HlsParser` with fixture playlists (masters with/without RESOLUTION, audio groups, byte ranges, keys, discontinuities, live); AES-128 decryption against a known vector; `ContainerSelector` table; `FfmpegRunner` progress parser (from captured sample output); segment downloader with a fake handler (retries, resume).
**Done when:** unprotected VOD HLS streams download at the selected quality into a playable file, DRM and live streams are clearly labelled, and the UI stays responsive during 1,000+ segment jobs.

---

### Phase 7 — Unprotected DASH and separate audio/video handling · **L**

**Goal / outcome:** For DASH (`.mpd`) videos, the picker lists video qualities and audio tracks. The download fetches both and merges them into one file.

**Depends on:** Phase 6 (segment downloader, FFmpeg, ContainerSelector).

**Checklist**
- [ ] `DashParser` (Core, `System.Xml.Linq`): `MPD@type` (static only; dynamic = live, unsupported), `Period` (single period first; multi-period is deferred or concatenated if the codecs match), `AdaptationSet`/`Representation`, `BaseURL` resolution at every level, `SegmentTemplate` (`$Number$`, `$Time$`, `$RepresentationID$`, `$Bandwidth$`, format tags like `%05d`), `SegmentTimeline`, `SegmentList`, `SegmentBase` (single file with an index range, fetched with Range), `mediaPresentationDuration` (ISO-8601 parser).
- [ ] `ContentProtection` present → "Protected (DRM) — not supported".
- [ ] Track model shared with HLS: `VideoTrack`, `AudioTrack` (language, channels, codec, bandwidth), `SubtitleTrack` (deferred).
- [ ] Picker: choose video quality and audio track (default: best audio in the user's language → highest bitrate). Show "Video + audio will be merged".
- [ ] Download video and audio as two segment jobs under one parent job (combined progress), then FFmpeg merge `-map 0:v -map 1:a -c copy` into the selected container.
- [ ] Apply the same separate-track logic to HLS audio renditions from Phase 6, if not already done.
- [ ] Also handle direct separate files (for example, a site serving `video-only.mp4` + `audio-only.m4a`) if detection can pair them confidently. Otherwise list them separately and labelled.

**Files:** `Core/Media/Dash/*`, `Core/Media/Tracks.cs`, `Core/Media/MediaDownloadPlanner.cs`, picker UI.
**Packages:** none new.
**Risks / edge cases:** huge `SegmentTimeline`s; `$Time$` arithmetic with `timescale`; mismatched audio/video durations; a `BaseURL` with multiple CDNs (pick the first, fall back); WebM DASH (VP9/Opus → WebM output); manifests with only a muxed representation.
**Manual verification:** DASH-IF reference test vectors (SegmentTemplate/Number, SegmentTimeline, SegmentBase, multi-audio) → correct track lists. Download → one playable file with sound. A DRM test vector → clearly refused.
**Automated tests:** `DashParser` fixtures for each segment addressing mode; template substitution with format tags; ISO-8601 duration parser; track pairing logic; container selection for VP9+Opus vs. H.264+AAC.
**Done when:** unprotected static DASH VOD downloads with selectable video quality and audio track into one playable file.

---

### Phase 8 — Ad-block upgrade: adblock-rust engine (optional) · **L**

**Goal / outcome:** Replace or supplement the Phase 2.5 extension with Brave's engine: a toolbar shield showing the blocked count, a per-site toggle, a global toggle, an allowlist, automatically updated community filter lists, and **scriptlet injection** so YouTube-style ads stay blocked.

**Do this only if** the Phase 2.5 extension's limits matter in practice (decision 2026-11-26). **They already show:** YouTube's "Experiencing interruptions?" notice with uBO Lite (open issue F16).

**Depends on:** Phase 2.5, Phase 3 (site settings). Can run in parallel with Phases 5–7 if desired.

**Checklist (assuming the recommended adblock-rust path; adjust per §12)**
- [ ] Native shim: `native/adblock-shim/` Rust crate exposing a C ABI (`engine_new_from_lists`, `engine_serialize/deserialize`, `engine_check(url, source_url, request_type) -> {matched, exception, redirect?}`, `engine_url_cosmetic_resources(url)`, `engine_hidden_class_id_selectors(classes, ids, exceptions)`, `free_*`). Prebuilt `adblock_shim.dll` (x64) committed via LFS or produced by a CI step. Document the build.
- [ ] `IFilterEngine` (Core) + `AdblockRustEngine` (P/Invoke, `SafeHandle`, UTF-8 marshalling, thread-safety rules).
- [ ] `FilterListUpdater` (Core): list catalogue (EasyList, EasyPrivacy, optional uBlock filters / Peter Lowe), conditional GET, `! Expires:` (default 4 days), background update on startup + timer, keep the last good copy, compile to a serialised engine file, hot-swap the engine atomically.
- [ ] `RequestBlocker` (Browser): filter registration with source kinds (worker kinds on one WebView only). Map `CoreWebView2WebResourceContext` → engine request type. Decision → `CreateWebResourceResponse(null, 403?, "Blocked", "")` (or redirect resources later). **No allocations-heavy work in the handler.**
- [ ] `BlockingPolicy` (Core): global toggle, per-site disable (by host, later eTLD+1), and an allowlist UI. The decision is cached per navigation.
- [ ] Scriptlet injection: get the scriptlet code for each page from the engine (`url_cosmetic_resources` → injected script, with uBO's scriptlet resources loaded; GPLv3) and inject it before page scripts run (`AddScriptToExecuteOnDocumentCreatedAsync`, all frames). Solve the timing: scripts are registered ahead of navigation, not per URL. This is what removes YouTube ads.
- [ ] Cosmetic filtering: on document creation and per frame, inject a domain-specific hide stylesheet. Generic class/id selector script with `MutationObserver`, using batched, debounced messages to the host. Validate messages (untrusted), cap sizes.
- [ ] UI: shield button with a count (per tab, per navigation; the count is batched). The popup shows the count, a "Blocking on this site" toggle (reloads the page), a link to settings.
- [ ] Settings page: global toggle, lists (enable/disable, last updated, update now), allowlist management, tracking prevention level (Balanced/Strict), list attributions.
- [ ] Performance instrumentation (DEBUG): handler time histogram, requests/sec, logged on demand.
- [ ] **F16:** YouTube plays without the repeated "Experiencing interruptions?" notice and without ads (first re-test with the current uBO Lite on/off to confirm the cause).

**Files:** `native/adblock-shim/*`, `Core/Blocking/*`, `Browser/RequestBlocker.cs`, `Browser/Scripts/cosmetic.js`, `ViewModels/ShieldViewModel.cs`, `Views/Controls/ShieldPopup.xaml`, settings UI.
**Packages/tools:** adblock-rust (MPL-2.0) via the shim; Rust toolchain (build-time only). If the extension path is chosen instead: the unpacked extension shipped in app files, with its license and source link.
**Risks / edge cases:** site breakage (the allowlist and per-site toggle are the answer; consider an "Report broken site" note); anti-adblock walls; first-party or server-inserted ads (not blockable); YouTube-style in-stream ads (largely not blockable at the network layer without scriptlets, so be honest in docs); handler latency on heavy pages; worker coverage; the engine rebuild must not block startup (load the cached serialised engine first); filter list downloads failing (keep the old copy).
**Manual verification:** visit ad-heavy news sites with the toggle on/off → visible difference and the count increments. Disable on one site → the ads return there only. Check that a typical news site's scrolling stays smooth. Use public ad-block test pages to compare coverage. "Update lists now" changes the timestamp.
**Automated tests:** policy decisions (global/per-site/allowlist precedence); list updater (expiry parsing, conditional GET via a fake handler, last-good fallback); engine wrapper smoke tests with a tiny fixture list (block, exception `@@`, `$third-party`, `$domain=`); resource-context mapping.
**Done when:** blocking is effective on common sites, measurably fast (DEBUG histogram p99 < 1 ms), toggles work instantly per site, and lists update in the background without UI impact.

---

### Phase 9 — UI polish, accessibility, performance and resilience · **M–L**

**Goal / outcome:** A clean, modern, consistent app that's keyboard- and screen-reader-friendly, scales well, and survives failures.

**Depends on:** Phases 1–8 (polish can start earlier in slices).

**Checklist**
- [ ] Light/dark/system theme switching at runtime. The WebView `PreferredColorScheme` follows it. Consistent spacing scale (4/8/12/16), typography (Segoe UI Variable, falling back to Segoe UI), icon sizes.
- [x] Tabs in the title bar via `WindowChrome` (caption buttons, drag regions, snap layouts on Windows 11, maximised border handling). *Done early (2026-11-26, your request after Phase 3):* `WindowChrome` (caption 40, resize border 6, `GlassFrameThickness 0,0,0,1` keeps the DWM shadow); own minimize/maximize/close buttons (`CaptionButtonStyle`, close turns red); tabs and "+" opt in with `IsHitTestVisibleInChrome`; a 48 px empty strip always stays draggable; maximized content is padded by the real frame size (`GetSystemMetricsForDpi(SM_CXSIZEFRAME + SM_CXPADDEDBORDER)`); fullscreen removes the chrome and restores it after; a `WM_NCHITTEST` hook answers `HTMAXBUTTON` over our maximize button so **Windows 11 snap layouts** appear (hover/press look and click handled from the non-client messages). Checked with real mouse input: drag moves the window, double-click maximizes, Restore down, F11 in and out, snap-layout flyout on hover, maximize click. Not yet checked: multi-monitor and other scaling levels (100/150/200%).
- [ ] Tab drag-to-reorder. Optionally tear off to a new window (deferred).
- [ ] Accessibility: `AutomationProperties.Name` on all icon buttons, logical tab order, visible focus rectangles, high-contrast theme support, Narrator pass.
- [ ] DPI: per-monitor V2 awareness (default in .NET WPF; verify in the manifest), test at 100/150/200% and when moving between monitors.
- [ ] Window state persistence (size, position, maximised; handle a disconnected monitor).
- [ ] Performance: suspend background tabs after N minutes (`TrySuspendAsync`), lazy session restore, virtualised lists (downloads/history), startup time measurement.
- [ ] Resilience: browser process failure recovery for all tabs, DB corruption recovery, FFmpeg crash handling, network loss UX, low disk space warnings.
- [ ] Empty states and helpful copy everywhere (no downloads yet, no videos detected, with an explanation of why some videos can't be detected).
- [ ] New Tab page: a simple internal page (search box, top sites from history).

**Files:** `Themes/*`, `Views/*`, `MainWindow.xaml` (WindowChrome), `app.manifest`.
**Packages:** none expected (re-evaluate WPF-UI only if the in-box Fluent theme is insufficient).
**Risks:** WindowChrome edge cases (maximised overflow, snap, touch); airspace with any overlay animations.
**Manual verification:** toggle themes live; navigate only with the keyboard; Narrator reads the toolbar buttons; drag the window between 100% and 150% monitors; kill `msedgewebview2.exe` processes in Task Manager → recovery UI; 30 tabs open → memory stays reasonable with suspension.
**Automated tests:** limited (UI). Keep the logic in Core tested; optionally add UI smoke tests later (FlaUI) — deferred.
**Done when:** a polish checklist pass on light/dark at 100/150/200% DPI, a keyboard-only pass, and a recovery pass all succeed.

---

### Phase 10 — Packaging and release readiness · **M**

**Goal / outcome:** An installable, updatable, signed AJBrowser that verifies its prerequisites and complies with dependency licenses.

**Depends on:** all features you want in v1.

**Checklist**
- [ ] Publish profile: `win-x64`, Release. Decide **self-contained** (no .NET prerequisite, larger) vs **framework-dependent** (smaller; the installer must ensure the .NET 10 Desktop Runtime). Recommendation: self-contained.
- [ ] Installer + updater: evaluate and choose (Velopack recommended; verify its current state). Per-user install (no admin), Start menu shortcut, uninstall that optionally removes `%LOCALAPPDATA%\AJBrowser`.
- [ ] WebView2 runtime: check at startup (Phase 1). The installer runs the Evergreen bootstrapper if the runtime is missing (preinstalled on Windows 11 and most updated Windows 10).
- [ ] FFmpeg distribution: either **bundle** an LGPL build (pin the version, include `LICENSE`/`COPYING.LGPLv2.1`, build configuration, and a source link/offer in About) or **download on first use** from a pinned URL with SHA-256 verification. Never ship a GPL build unless AJBrowser's licensing allows it.
- [ ] Third-party notices file (WebView2 SDK license, CommunityToolkit, SQLite, adblock-rust MPL-2.0 + source location, filter list attributions, FFmpeg).
- [ ] Code signing (for example, Azure Trusted Signing or an OV certificate) to reduce SmartScreen warnings. Sign the exe, the shim DLL, and the installer.
- [ ] Update channels: app updates via the updater; **WebView2 engine updates come from Microsoft independently**; filter lists update in-app; FFmpeg updates ship with app releases (or a pinned on-demand version bump). Document this in About.
- [ ] Optional: register as a default-browser candidate (URL associations through registry Capabilities), deferred unless wanted.
- [ ] Release checklist: version bump, changelog, clean-machine test (Windows 10 + 11 VMs, without the .NET SDK), upgrade from the previous version, uninstall.

**Files:** `build/` scripts, `AJBrowser.csproj` publish properties, `THIRD-PARTY-NOTICES.md`, installer config.
**Packages/tools:** Velopack (or the chosen installer), signing tooling.
**Risks:** SmartScreen reputation for new certificates; AV false positives (especially downloader apps launching FFmpeg); license compliance mistakes.
**Manual verification:** install on a clean VM → it runs, browses, downloads, and blocking works. Publish v1.0.1 → the app updates itself. Uninstall → it's removed cleanly.
**Done when:** a non-developer can install, use, update and uninstall AJBrowser on a clean Windows 10/11 machine, and all license notices are present.

---

## 8. Known limitations (by design)

- No DRM circumvention (Widevine, PlayReady, FairPlay, `SAMPLE-AES`).
- No guarantee for any specific site. Detection depends on what the network reveals.
- Live streams aren't supported initially.
- Server-side inserted ads, first-party ads and anti-adblock walls may not be blockable.
- WebSocket and some browser-internal traffic isn't interceptable by WebView2.
- Browser-managed downloads don't resume after an app restart. App-managed downloads resume only when the server supports Range requests.
- Some Chrome features are missing in WebView2 (extension UI, sync, profiles UI, built-in PDF tools may differ).
- WPF airspace: no WPF overlays on top of web content. We use popups and docked panels instead.

---

## 9. Deferred and optional features

- [ ] **Site-specific extractors** (for example, sites using custom APIs or signature schemes). Optional and later only. Each extractor breaks whenever the site changes, may violate terms of service, and needs ongoing maintenance. If ever added, isolate them behind an `IMediaExtractor` interface, keep them opt-in, and consider an external tool approach instead of in-house code.
- [ ] Multiple named profiles (`ProfileName`) with a profile switcher.
- [ ] Private windows UI (environment support is designed in from Phase 1, and the window comes after Phase 3).
- [ ] Live HLS/DASH recording.
- [ ] Subtitle track download (WebVTT/TTML) and muxing.
- [ ] Optional "Convert" with re-encoding (licensing review needed for H.264 encoders).
- [ ] Import bookmarks and history from Edge/Chrome/Firefox.
- [ ] Bookmarks: drag-to-reorder (on the bar and the Bookmarks page), export/import as an HTML bookmarks file, and an overflow "»" menu for bar items that don't fit.
- [ ] Per-visit history (every visit with its own time) instead of one row per URL, if the day-grouped view needs it.
- [ ] Inline autocomplete in the address bar (complete "git" to "github.com" as you type).
- [ ] Per-site zoom and permission memory UI.
- [ ] Password manager integration (WebView2 autofill settings exist, but UI parity is limited).
- [ ] Tab tear-off and multi-window drag.
- [ ] Middle-click / Ctrl+click on a link opening a **background** tab. WebView2 reports these as ordinary user-initiated new windows, so they currently open in front.
- [ ] Scrollable tab strip (scroll buttons or wheel) when tabs reach their minimum width; they're clipped for now.
- [ ] Default-browser registration.
- [ ] ARM64 build.
- [ ] Sync (out of scope for a solo project unless a backend is chosen).
- [ ] `WebView2CompositionControl` (a WPF control without airspace limits, at a possible performance cost). **Unverified**; evaluate only if airspace becomes painful.

---

## 10. Items to verify

| # | Question | How | Status |
|---|---|---|---|
| V1 | Does `WebResourceResponseReceived` expose the content type, 206 ranges and the final URL after redirects? | Spike | ◐ Content type, length and range yes (F1). Redirects not yet observed. |
| V2 | Does `Response.GetContentAsync()` reliably return manifest bodies? | Spike | ☑ Yes, all of them (F2) |
| V3 | Do browser downloads continue when their WebView/tab closes? | Spike | ☑ **No**: the operation freezes and events stop (F5) |
| V4 | Is `WebResourceRequested` with `"*"` + All source kinds fast enough on heavy sites (UI thread)? | Spike | ☑ Yes (F6) |
| V5 | Can an MV3 blocker (uBO Lite) run in WebView2 via `AddBrowserExtensionAsync`, and how well? | Spike | ☑ Yes; it blocked YouTube video ads in Optimal mode (F15) |
| V6 | Does `NewWindowRequested` + `args.NewWindow` keep `window.opener` for OAuth popups? | Spike | ☑ Yes (F10) |
| V13 | Does `CookieManager.GetCookiesAsync` return HttpOnly cookies for a signed-in site? | Spike | ☑ Yes (F13) |
| V7 | Which keys does WebView2 consume vs. forward to WPF (`Ctrl+L`, `Ctrl+T`, `Ctrl+W`, `F5`, …)? Should we set `AreBrowserAcceleratorKeysEnabled`? | Phase 2 | ◐ Implemented assuming the WPF control forwards accelerator keys as `PreviewKeyDown` (and handling them stops WebView2's own action); `AreBrowserAcceleratorKeysEnabled` left on. **Your manual check pending.** |
| V8 | Is the WebView2 Find API available and stable in SDK 1.0.4370.26? | Phase 2 | ◐ Available (`CoreWebView2.Find`, `StartAsync`, `FindNext/Previous`, `MatchCount`, `ActiveMatchIndex`; checked by reflection). Still to verify by hand: whether `ActiveMatchIndex` is 1-based (the counter assumes it is). |
| V9 | How stable is the in-box WPF Fluent `ThemeMode` in .NET 10 for our controls? | Phase 0 | ◐ Builds and runs; visual check of Light/Dark/System pending |
| V10 | Velopack's current status and fit (self-contained WPF, per-user install, updates) | Phase 10 | ☐ |
| V11 | Is the ICoreWebView2_22 three-parameter filter available in the installed SDK and runtime? | Spike | ☑ Yes; ServiceWorker requests were intercepted (F7) |
| V12 | Exact WPF `EnsureCoreWebView2Async(environment, controllerOptions)` overload for private mode | Phase 1 | ☑ Exists and works with `ProfileName` (F11) |

**Verified from primary docs (2026-11-26):** `AddWebResourceRequestedFilter` with `CoreWebView2WebResourceRequestSourceKinds` (Document / SharedWorker / ServiceWorker; register worker kinds on only one WebView per environment); `AreBrowserExtensionsEnabled` + `AddBrowserExtensionAsync` (unpacked extension folder; extension UI entry points are missing in WebView2); WPF `ThemeMode` exists and is marked experimental (WPF0001). No maintained .NET binding for adblock-rust was found (its official bindings are Rust and JS, with community Python bindings).

---

## 11. Spike findings log

All findings: 2026-11-26, WebView2 runtime **158.0.4370.33**, SDK 1.0.4370.26, Windows 11 (10.0.26200) x64. They come from the automated spike suite run by the agent (reports in `%LOCALAPPDATA%\AJBrowser\Diagnostics\Reports\`).

| # | Question | Finding | Consequence for the plan |
|---|---|---|---|
| F1 | V1 media responses | Status, `Content-Type`, `Content-Length` and `Content-Range` are all visible. A `<video src=mp4>` produced a single `206` with `Content-Range: bytes 0-N/TOTAL`, so the total size is known. **Content types are unreliable:** the Mux HLS manifest came as `audio/mpegurl`, TS segments as `application/octet-stream` or with no type, and some Apple manifests had no `Content-Type`. **DASH segments arrive as `video/mp4` / `audio/mp4` with status 200** (42 in 25 s). **Apple's fMP4 HLS uses byte ranges into one `main.mp4`** (82 MB total via `Content-Range`), which looks exactly like a complete MP4. No 3xx responses were observed. CNN's homepage refreshed a **live** HLS manifest every ~4 s. | Phase 5: classify by content type **and** extension. Suppress anything referenced by a parsed manifest (including byte-range `main.mp4` files). Dedupe repeated live-manifest refreshes. Re-check redirects. |
| F2 | V2 manifest bodies | `Response.GetContentAsync()` returned the body for 100% of manifests (HLS master and media playlists, DASH MPD, CNN live). | Phase 5/6 can parse manifests straight from the event; a re-fetch is only a fallback. |
| F3 | Credential exposure | No `Cookie` or `Authorization` request headers appeared in `WebResourceResponseReceived` in the tests. **Inconclusive**: the test sites set no cookies. | Still assume headers may contain secrets: copy only allowlisted headers (plan §6.2). |
| F4 | Playback / blob URLs | WebView2 reports `canPlayType(HLS) = "maybe"`. Native HLS played the Mux TS stream but **failed** on Apple's fMP4 sample (`MEDIA_ERR_SRC_NOT_SUPPORTED`). With hls.js/dash.js, `currentSrc` is a `blob:` URL (3840×2160 DASH, 1280×720 HLS). | Confirms §6.3: detection must rely on network responses. The page-hint script can still report `videoWidth/Height`. |
| F5 | V3 downloads | `DownloadStarting` with `Handled = true` + custom `ResultFilePath` works. `Pause()` → `Interrupted / UserPaused`, `CanResume = true`; `Resume()` continues. While in progress the target file stays ~126 KB (data is written elsewhere until completion). **Closing the only WebView froze the operation**: `BytesReceived` stopped and no further events arrived. A navigation that turns into a download completes with `WebErrorStatus = ConnectionAborted`. | **Phase 4:** a tab with active downloads must keep its WebView alive (hidden) until they finish, or ask before closing. Never infer progress from file size. **Phase 1:** don't show an error page for the `ConnectionAborted` that accompanies a download. |
| F6 | V4 interception cost | Observe-only handler p50 6–13 µs. With `Request.Uri` read and a host match, p50 25–68 µs, p99 ≤ 440 µs, one 4.9 ms outlier. UI-thread lag p95 (17–29 ms, mostly timer noise) **did not change** between modes. CNN: 346 requests intercepted, 40 blocked by the 15-domain test list. | In-process network blocking is viable. Reading COM properties dominates the cost, so read `Request.Uri` once and avoid allocations. Budget ≈ 350 requests × 50 µs ≈ 18 ms spread over a page load. |
| F7 | V11 worker coverage | The three-argument filter works at runtime: `ServiceWorker` requests were intercepted on timesofindia (2–14 per load). | Use it in Phase 8, on one WebView per environment. |
| F8 | Tracking prevention | Strict vs. Balanced responses: CNN 387 → 91, Times of India 71 → 24, Guardian 25 → 26 (consent wall). | A strong free baseline. Offer Strict as a setting (may break some sites); keep Balanced as the default. |
| F9 | Benchmark caveats | Daily Mail aborted connections in every mode (bot protection or region), so it was excluded. Load times are noisy: heavy pages often never "complete" within 30 s because of streaming and ads. | Compare request counts rather than load times. |
| F10 | V6 popups | With `args.NewWindow` assigned inside a deferral, `window.opener` worked both ways, and `window.close()` raised `WindowCloseRequested`. A script popup had `IsUserInitiated = false` and **WebView2 did not block it**. **Per-WebView setup is not inherited by new windows** (the popup failed with `HostNameNotResolved` until its own virtual-host mapping was added). | **Phase 2:** implement our own popup blocking for non-user-initiated popups. Put all per-WebView setup (handlers, scripts, filters, mappings) in one `BrowserTab` initialisation used for **every** WebView, including popups. |
| F11 | V12 controller options | `EnsureCoreWebView2Async(environment, controllerOptions)` with `ProfileName` works for both the main WebView and popups. | Phase 1 can use named profiles and InPrivate as designed. |
| F12 | Logging privacy | CNN's live HLS URLs carry long tokens **in the path** (`/live/use1/d17_Cixj…`), which `Redactor.Url` keeps (it strips only query and fragment). | Phase 5: add `Redactor.MediaUrl` that shortens long or opaque path segments, and use it for all media URLs in logs and history. |
| F13 | V13 cookies (manual, by you, 2026-11-26) | Signed in to GitHub inside the Diagnostics profile: `GetCookiesAsync("https://github.com/")` returned 18 cookies, **12 HttpOnly**, 18 Secure, 8 session cookies, 2 distinct domains. | App-managed downloads (Phase 5+) can send the same session cookies the browser would, including HttpOnly ones, so sign-in-protected media can work. Fetch them per exact URL at download start; never log or persist them. |
| F15 | V5 extension (manual, by you, 2026-11-26) | `AddBrowserExtensionAsync` loaded **uBlock Origin Lite** (id `lelebihonlheieljmpcmfflcmlelhfob`) with `AreBrowserExtensionsEnabled = true`. Its dashboard opened at `chrome-extension://<id>/dashboard.html`; default filtering mode **Optimal**. **YouTube pre-roll ads were blocked with the extension on and appeared with it off.** The profile stores it as an *unpacked* extension (Chromium location 4) that **references the original folder** (`D:\tools\uBOLite`); WebView2 does not copy it, so the folder must stay in place. No extension toolbar button or popup exists in WebView2 (as documented). | Option B works, including YouTube, which network-only blocking (C without scriptlets, or D) would largely miss. Shipping it means: enable extensions in `BrowserEnvironment`, bundle the folder in the app directory (GPLv3 notice + source link), install on startup if missing, build our own on/off UI. **Rules only update with new extension versions** (MV3 static rulesets), so updates ride on AJBrowser releases. Our own blocked counter isn't available from the extension. |
| F17 | **Bug, fixed:** popup tab closing itself closed the whole browser (your report, 2026-11-26) | Clicking a link that opened a new tab which then closed itself (a download link opened in a new tab: Chromium closes a popup that only started a download; also any page calling `window.close()`, e.g. sign-in popups) exited the app. Cause, from a stack trace: the **WPF WebView2 control's own** `WebView2Base.CoreWebView2_WindowCloseRequested` calls `Window.Close()` on the window it's in; there's no option to disable it and it runs before our handler. Present since Phase 2 (verified on older builds). Also found: with a page focused, **Alt+F4** reaches WebView2, which turns it into the same close request. | `MainWindow.OnWindowClosing` cancels a close that comes from that handler (stack frame in the WebView2 WPF assembly named `…WindowCloseRequested`); `BrowserTab` still closes just the popup tab (now posted after the event, never disposing a WebView inside its own event). Alt+F4 is handled as our own shortcut (closes the window before WebView2 sees it). Checked: self-closing popup and download-in-new-tab popup close only their tab; Alt+F4 with a page or internal page focused and the ✕ button still close the browser. Re-check if the WebView2 SDK is updated (the method name is what we match). |
| F18 | 4c keep-alive spike (agent, standalone scratch app, own WebView2 data folder, local server sending 40 MB at ~3 MB/s) | After 2 s of downloading: **Dispose** → frozen at 4.98 MB for 25 s (F5 reproduced). **Removed from the visual tree, not disposed** → kept going to Completed. **Muted + navigated to about:blank, then removed** → also Completed, same speed. Pause → `Interrupted/UserPaused`, `CanResume = True`; Resume → InProgress, still out of the window. | Phase 4c keeps a closed tab's WebView (page unloaded via about:blank) until its downloads end, then disposes it. Not yet re-checked inside AJBrowser itself. |
| F14 | Popup gesture flag (manual) | Clicking the **Open popup** button gave `IsUserInitiated = True`; the automated script-only popup gave `False` (F10). | The flag reliably separates user-clicked popups from script popups, so Phase 2's popup blocker can rely on it. |
| F16 | **Open issue:** YouTube "Experiencing interruptions?" (manual, by you, 2026-11-26, normal browser after Phase 2.5) | While loading videos on YouTube, its player repeatedly shows the notice "Experiencing interruptions?". The text isn't in AJBrowser's code (searched), so YouTube's page shows it. **Suspected cause: YouTube reacting to uBO Lite** (ad-block detection/interference). Not yet confirmed with the on/off test (shield → switch off → reload → does it go away?). | Accepted for now; not fixed in Phase 2.5. Things to try later: newer uBO Lite release, the "Complete" filtering mode. Expected real fix: **Phase 8** (our own engine + scriptlet injection, updated independently of extension releases). Re-test before starting Phase 8. |

**Ad-block engine: summary so far.** Network interception performs well (F6, F7), so an engine running inside our app is viable (options C or D in §6.5). The extension route (option B) **works, including YouTube video ads** (F15), at little cost, but with weaker integration (no counter, own toggle UI needed) and rule updates tied to releases. Option C (adblock-rust, Brave's engine) gives full integration and background list updates; matching Brave on YouTube additionally needs **scriptlet injection** (adblock-rust supplies the scriptlet code; we inject it with `AddScriptToExecuteOnDocumentCreatedAsync`), plus a Rust toolchain, which isn't installed yet.

---

## 12. Decision log

| Date | Decision | Reason |
|---|---|---|
| 2026-11-26 | Keep .NET 10 / WPF / WebView2 Evergreen / CommunityToolkit.Mvvm / Microsoft.Data.Sqlite | Matches the existing project and the requirements |
| 2026-11-26 | Two projects (App + Core) plus a test project | Testability without over-architecture |
| 2026-11-26 | No EF Core, DI container or logging framework initially | Minimise dependencies; revisit if needed |
| 2026-11-26 | FFmpeg as an external LGPL process, introduced in Phase 6 | Remux only; license-friendly; not needed for direct files |
| 2026-11-26 | App-managed HLS/DASH segment download (not FFmpeg network input) | Keeps cookies in-process, precise progress, resumable |
| 2026-11-26 | Ad blocking: ship **uBlock Origin Lite** via WebView2's extension API in a new **Phase 2.5** (right after tabs); **adblock-rust** + scriptlets becomes an optional Phase 8 upgrade | F15: uBO Lite blocked YouTube ads in WebView2 at little cost. Limits accepted for now: no blocked counter, rules update only with new extension versions, our own on/off UI needed. Brave's approach (C) is the path if those limits bite. |
| 2026-11-26 | No `global.json` for now | Only the .NET 11 SDK is installed locally; it builds net10.0. Revisit when CI is added. |
| 2026-11-26 | Theme: in-box Fluent `ThemeMode` + own semantic brush dictionaries (tentative) | No dependency; switch to custom-only or WPF-UI if the V9 visual check finds problems |
| 2026-11-26 | Phase 1 defaults: Bing as home page and search engine (`BrowserDefaults`) | Placeholder until the search-engine and homepage settings in Phase 3 |
| 2026-11-26 | Address input: typed `file://`, `data:`, `about:` allowed; `javascript:`/`vbscript:` blocked; text with spaces is always a search; bare hosts use https, localhost/IPs/single-word-with-port use http | Matches mainstream browsers; no Public Suffix List needed yet |
| 2026-11-26 | Error panel only for network-level failures with no HTTP response | A site's own 404/500 page is more useful; cert errors keep WebView2's own page; avoids false error panels for Stop and downloads |
| 2026-11-26 | Phase 1 popups: user-clicked new windows open in the same page, script popups are blocked | No tabs yet, and WebView2-created windows would skip our setup (F10). Replaced in Phase 2 |
| 2026-11-26 | Phase 2: user-initiated popups become tabs via deferral + `NewWindow` (keeps `window.opener`); non-user popups blocked with a toolbar indicator | WebView2 doesn't block popups (F10); `IsUserInitiated` is reliable (F14) |
| 2026-11-26 | Own find bar (docked above the page) instead of WebView2's find dialog | Works when focus is in our UI, matches the app theme, and avoids depending on WebView2's dialog behaviour |
| 2026-11-26 | Keep WebView2's built-in link-hover status bubble and permission prompts | They already do what the plan asked; a custom `Popup` would add airspace/positioning bugs for no gain |
| 2026-11-26 | WebView `DefaultBackgroundColor` is always white | Chrome's behaviour: pages without their own background assume white; a dark default made them unreadable in dark mode (found in Phase 2 testing) |
| 2026-11-26 | Phase 2.5: ship uBO Lite as its **official release zip** (committed to git, 9 MB) and unpack it at first run, rather than committing the unpacked folder or downloading it at build time | One unmodified upstream file is the clearest GPL "unmodified aggregation" story and keeps git to one binary instead of ~1,000 files; no network needed to build. Upgrade = drop in a newer zip and delete the old one |
| 2026-11-26 | Phase 2.5: ad-block on/off and the installed version live in `Extensions\uBOLite\state.json` until Phase 3 settings | WebView2 doesn't report extension versions; an explicit file makes "restart keeps the choice" independent of how WebView2 persists the enabled flag |
| 2026-11-26 | Phase 3 schema v1 has only `history`, `bookmarks`, `session_tabs`; `downloads` (Phase 4) and `site_settings` (Phase 8) come as later migrations | Forward-only migrations make adding tables cheap; empty tables designed before their feature exists would likely need changing anyway |
| 2026-11-26 | History keeps one row per URL (last visit + count), as planned, rather than one row per visit | Simple, small and fast; "group by day" shows each page on its latest day. Revisit if per-visit history is wanted |
| 2026-11-26 | Ad-block on/off stays in the extension's `state.json`, not `settings.json`; Settings shows the same switch as the shield (shared `AdBlockViewModel`) | One source of truth next to the extension id and version it belongs to |
| 2026-11-26 | Internal pages use `ajbrowser://` addresses, are WPF views inside a tab, and the WebView starts lazily (also for restored background tabs) | Typeable and restorable like Chrome's chrome:// pages, without exposing any host bridge to web content; restored sessions stay cheap |
| 2026-11-26 | Settings apply immediately (no Save button); text fields apply on Enter or leaving the box, with inline validation | Matches Chrome/Edge settings; no "unsaved changes" state to manage |
| 2026-11-26 | Site icons are stored per **host** in SQLite (migration v2), captured from the tabs' own favicons; no third-party favicon service | Covers History/Bookmarks/suggestions with no extra network requests and no privacy leak (asking e.g. Google's favicon service would reveal every visited site) |
| 2026-11-26 | Sharper site icons: AJBrowser itself downloads a site's icon file (once per site and session, only while the stored icon is under 32 px), cookie-free and size-capped; SVG icons are not fetched | WebView2 only exposes 16 px favicons, which blur on scaled displays. Same request the page already made to the same site, so no new party learns anything; skipping SVG keeps theme-aware icons correctly coloured. Proper SVG rendering would need a library (deferred) |
| 2026-11-26 | Remove the system title bar now (tabs in the title bar, Phase 9 item done early) | Your request; saves ~30 px and matches Chrome/Edge. Done with WPF's in-box `WindowChrome` plus a small hit-test hook for snap layouts, no new package |
| 2026-11-26 | Phase 4b: the download button is always on the toolbar; the flyout opens by itself when a page starts a download (not in fullscreen) | Discoverable, matches Edge; the ring shows progress without opening anything |
| 2026-11-26 | Phase 4b: "Copy link" copies the full address for browser downloads and the shortened (redacted) one for app downloads | A browser download's link is the user's own (Chrome copies it in full); app downloads (detected media, Phase 5) often carry signed tokens |
| 2026-11-26 | Phase 4b: Retry of a browser download opens its address in a new foreground tab and removes the old entry | WebView2 has no "download this URL" API, so only a navigation can restart it; a visible tab shows what happened if the link now leads to a page or an error. The blank tab left behind is closed automatically in 4c |
| 2026-11-26 | Phase 4b: a browser download interrupted by network trouble, shutdown or a crash is **Interrupted** (Resume offered while the operation lives); disk, server and security failures are **Failed** | Matches the state model (Interrupted = outside the user's control, often resumable) |
| 2026-11-26 | **Bug fix after 4b (your report):** Pause is offered on every running download, and a user-paused download can always be resumed; WebView2's `CanResume` only gates resuming after an *error* | `CanResume` is false while a download runs (WebView2 sets it only once a download has stopped), so the Pause button never appeared. `Resume()` works on user-paused downloads; without server range support it starts over (like Chrome) |
| 2026-11-26 | Speed estimator keeps its newest sample in a field (O(1)) | LINQ `Last()` on a `Queue` walked every sample on every WebView2 progress event |
| 2026-11-26 | Phase 4c: closed tabs with downloads keep their WebView parked (about:blank, muted) until the downloads end, rather than asking before closing the tab | F18 shows it works; matches Chrome (closing a tab never stops its downloads). Asking happens only at app exit, where the engine really ends |
| 2026-11-26 | Phase 4c: exit question only for browser downloads; app downloads pause silently and resume next start | Only browser downloads are lost on exit |
| 2026-11-26 | Phase 4c: old `.ajpart` cleanup looks only at the top level of the download folders, files older than 7 days, and always asks | Never delete user files without asking; `.ajpart` is AJBrowser's own extension |
| 2026-11-26 | Phase 5a: a direct media file counts as a complete video only when the browser's own player (or a navigation) loaded it and it's ≥ 1 MB, or the page's &lt;video&gt; reported it; script-fetched (Fetch/XHR) files never count by size alone | MSE players fetch stream pieces with fetch/XHR, and some pieces are byte ranges of one big MP4 (spike F1) that would otherwise show up as a fake 82 MB "video" |
| 2026-11-26 | Phase 5a: HLS/DASH streams are listed in the picker already (labelled "supported soon" until Phases 6/7) | Shows the user something was found instead of an empty list, without a segment flood |
| _pending_ | Installer/updater | Decide in Phase 10 |

---

## 13. How to continue with the coding agent

1. **Start each session** by asking the agent to read `PROGRESS.md` (short handoff summary), then `IMPLEMENTATION_PLAN.md` (this file) and the current code. Name the phase or task, for example: *"Implement Phase 1, items 1–4."*
2. **One phase (or a slice of one) per session.** Don't skip ahead. Later phases assume earlier checklists are done.
3. **The agent must update this file** in the same change as the code:
   - tick completed checkboxes (`- [x]`);
   - update **Current phase** at the top;
   - add spike results to §11 and decisions to §12;
   - move anything discovered to §9 (deferred) or §10 (verify) rather than silently expanding scope;
   - update `PROGRESS.md` (current state, next task, last updated).
4. **The agent never commits.** The user reviews and commits.
5. **Verification is explicit:** the agent states what it built, what it ran (build, tests), and what you must check manually in Visual Studio. It never claims to have run the app unless it did.
6. **Before a new dependency** is added, the agent explains why and updates §3. New packages need a reason.
7. **You commit per completed slice** (after reviewing), with a message referencing the phase (for example, `Phase 1: address bar and navigation commands`).
8. If reality contradicts the plan (an API doesn't behave as documented), **update the plan first**, then the code.
