# AJBrowser — Implementation Plan

> Living roadmap for AJBrowser, a Windows desktop browser built with C#, WPF and WebView2.
> Tick the checkboxes as work lands. See [How to continue with the coding agent](#13-how-to-continue-with-the-coding-agent) at the end.

**Plan version:** 1.0 · **Created:** 2026-11-26 · **Current phase:** Phase 0 (not started)

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
| Microsoft.Data.Sqlite | ✅ Add in Phase 3 | MIT. Use plain SQL with small repository classes. **No EF Core** (heavy, adds migration tooling, overkill here) and **no Dapper** for now (optional later). |
| HttpClient | ✅ In-box | Used for app-managed downloads, manifest fetching, and filter-list updates. Use one shared `SocketsHttpHandler`-based client per profile context. |
| FFmpeg / FFprobe | ✅ Add in Phase 6 (not earlier) | Run as an **external process**, never linked. Use an **LGPL build** (no `--enable-gpl`), because we only need remuxing, no x264 encoding. Distribution details are in Phase 10. Direct MP4/WebM downloads (Phase 5) don't need FFmpeg. |
| DI container | ❌ Not initially | Use a hand-written composition root (`AppServices`) built in `App.OnStartup`. It's easier to read for someone new to C#. Revisit `Microsoft.Extensions.DependencyInjection` if wiring becomes painful. |
| Logging framework | ❌ Not initially | A small in-house `ILogSink` that writes to a rolling file, with a **mandatory redaction helper**. Avoids Serilog/NLog until needed. |
| Theme library | ⚖️ Decide in Phase 0 | Option A (recommended first): WPF's in-box **Fluent theme** (`Application.ThemeMode` = Light/Dark/System). It still carries the `[Experimental]` diagnostic **WPF0001** in .NET 9/10, so it must be suppressed. Option B: custom resource dictionaries only. Option C: WPF-UI (lepoco, MIT) if A proves too buggy. Our custom chrome (tabs, toolbar) uses our own semantic brush keys either way, so switching later is cheap. |
| Icons | ✅ In-box | Segoe Fluent Icons (Windows 11) with a Segoe MDL2 Assets fallback (Windows 10). No icon package needed. |
| Ad-block engine | ⚖️ Decide after the Phase 0.5 spike | See §6.5. Leading candidates: **adblock-rust** (Brave, MPL-2.0) through a small native C-ABI shim, or loading a maintained MV3 blocker extension through WebView2's extension API. Built-in **WebView2 tracking prevention** is a free baseline from Phase 1. |
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

**Recommendation:** A from Phase 1 (free). In Phase 8 use **C (adblock-rust)** as the primary engine. If the spike shows that B works well and you'd rather avoid Rust, B with A is an acceptable lighter path, at the cost of integration. D is a fallback only.

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
- [ ] Create `AJBrowser.Core` (net10.0 class library) and `AJBrowser.Core.Tests` (xUnit). Add both to `AJBrowser.slnx`, and reference Core from the app and the tests.
- [ ] Add `CommunityToolkit.Mvvm` to the app.
- [ ] Add a `Directory.Build.props` (shared `LangVersion`, `Nullable`, `TreatWarningsAsErrors` for Core only, optional).
- [ ] Optional: add `global.json` (SDK pin / roll-forward policy).
- [ ] Replace `StartupUri` with `App.OnStartup` that builds `AppServices` and shows `MainWindow`.
- [ ] Add a global exception handler (`DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`, `AppDomain.UnhandledException`) that logs (redacted) and shows a friendly dialog.
- [ ] `AppPaths` (Core) creates the folders from §5.6.
- [ ] `ILogSink` + `FileLogSink` + `Redactor` (Core), with tests for redaction.
- [ ] Theme decision: try `Application.ThemeMode` (suppress WPF0001 locally). Create `Themes/` with semantic brush keys (`AppBackgroundBrush`, `ToolbarBrush`, `TabActiveBrush`, …).
- [ ] Set the window title "AJBrowser", a sensible default size (1280×800), min size, and app icon placeholder.
- [ ] Add a `README.md` with build/run instructions.

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

**Checklist**
- [ ] Host a WebView2 using an explicit environment (user data folder from `AppPaths`). Log `CoreWebView2Environment.BrowserVersionString`.
- [ ] **Media responses:** log `WebResourceResponseReceived` (URL redacted, status, content type, lengths, resource context) while playing: a page with `<video src=*.mp4>`, Apple's public HLS sample streams, and DASH-IF test vectors. Confirm content types are visible, `206` ranges appear, and how redirects are reported.
- [ ] **Manifest body:** check that `args.Response.GetContentAsync()` returns the `.m3u8`/`.mpd` body. Note failures.
- [ ] **Cookies:** call `CookieManager.GetCookiesAsync(url)` for a media URL on a logged-in test site (your own). Confirm HttpOnly cookies are returned. Don't log values.
- [ ] **Downloads:** handle `DownloadStarting` (`Handled = true`, custom `ResultFilePath`). Test pause/resume/`CanResume`. **Close the WebView while downloading:** does it continue?
- [ ] **Blocking:** add `AddWebResourceRequestedFilter("*", All, Document|SharedWorker|ServiceWorker)`. Block a hard-coded test domain list. Measure handler count and timing on 3 heavy news sites, and check UI responsiveness (scrolling, typing in a TextBox).
- [ ] **Tracking prevention:** compare request counts with `PreferredTrackingPreventionLevel` at Balanced vs. Strict.
- [ ] **Extension path:** create the environment with `AreBrowserExtensionsEnabled = true`, load an unpacked MV3 blocker (for example, uBO Lite) via `AddBrowserExtensionAsync`, and check that blocking works without its popup.
- [ ] **New windows:** `NewWindowRequested` with deferral + `args.NewWindow`. Verify that an OAuth-style popup keeps `window.opener`.
- [ ] Optional: build a minimal adblock-rust C-ABI shim and call it once from C# (proves the toolchain).
- [ ] Record findings in §11 and make the ad-block engine decision in §12.

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
- [ ] `BrowserEnvironment` creates the shared environment once (async, at startup), with the user data folder from `AppPaths`.
- [ ] Runtime check: if `GetAvailableBrowserVersionString` fails, show a dialog with the official WebView2 runtime download link and exit gracefully.
- [ ] `BrowserTab` wraps the WebView2 control: `EnsureCoreWebView2Async(environment)`, and exposes `Navigate`, `GoBack`, `GoForward`, `Reload`, `Stop` plus events (`UrlChanged`, `TitleChanged`, `LoadingChanged`, `NavigationFailed`, `HistoryChanged`).
- [ ] `UrlOrSearchResolver` (Core): handles a scheme present, bare domain (`example.com`), `localhost:port`, IP addresses, and search text → search URL. **Test heavily.**
- [ ] `TabViewModel` + `MainWindowViewModel` with toolbar commands, and `CanExecute` tied to `CanGoBack`/`IsLoading`.
- [ ] Toolbar UI: Back, Forward, Reload/Stop (toggle), Home, address bar (selects all on focus; shows the full URL while editing and the clean URL otherwise; Enter navigates; Esc reverts).
- [ ] Loading indicator (thin progress bar or spinner on reload).
- [ ] Errors: `NavigationCompleted.IsSuccess == false` → show a friendly in-view message (hide the WebView to avoid airspace issues) with Retry, mapped from `WebErrorStatus`. Keep WebView2's own cert-error page.
- [ ] `ProcessFailed`: renderer crash → "This page crashed — Reload". Browser process exit → recreate the environment and WebView, then reload.
- [ ] Set `PreferredTrackingPreventionLevel = Balanced` (Strict as a later setting).
- [ ] Set `PreferredColorScheme` to follow the app theme.
- [ ] Disable WebView2 dev tools and the default context-menu items **only in Release** if desired (keep DevTools in Debug).

**Files:** `Browser/BrowserEnvironment.cs`, `Browser/BrowserTab.cs`, `ViewModels/MainWindowViewModel.cs`, `ViewModels/TabViewModel.cs`, `Views/MainWindow.xaml`, `Views/Controls/AddressBar.xaml`, `Core/Navigation/UrlOrSearchResolver.cs`.
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
- [ ] `ObservableCollection<TabViewModel>` + `SelectedTab`. Each tab owns its `BrowserTab`.
- [ ] Content host: all tab WebViews live in one `Grid`, and only the active one is `Visible` (preserves page state). Dispose the WebView on close.
- [ ] Tab strip control: favicon, title (ellipsis, tooltip), close button, loading spinner, middle-click to close, "+" button, horizontal overflow (scroll or shrink). Drag-to-reorder is deferred to Phase 9.
- [ ] Favicons via `FaviconChanged` → `GetFaviconAsync(PNG)` → `BitmapImage` (frozen), with a fallback icon.
- [ ] `NewWindowRequested`: take a deferral, create a tab next to the opener, `args.NewWindow = newTab.CoreWebView2`, complete. Respect `args.IsUserInitiated` (block non-user popups with an indicator). Popup windows with `WindowFeatures` may open as tabs for now.
- [ ] Closed-tab stack (URL, title, index; last 25). `Ctrl+Shift+T` reopens.
- [ ] Keyboard shortcuts: `Ctrl+T`, `Ctrl+W`, `Ctrl+Shift+T`, `Ctrl+Tab`/`Ctrl+Shift+Tab`, `Ctrl+1…8`, `Ctrl+9` (last), `Ctrl+L`/`Alt+D`/`F6` (focus address bar), `Alt+←/→`, `F5`/`Ctrl+R`, `Esc` (stop), `Ctrl+F`, `Ctrl+±/0` (zoom), `F11`, `Ctrl+P`. Shortcuts pressed while the WebView2 has focus arrive through the WPF control's key events / `AcceleratorKeyPressed`. **Verify which keys WebView2 consumes** (§10).
- [ ] Find in page: use WebView2's Find API if available in the installed SDK (verify), else defer.
- [ ] Zoom per tab (`ZoomFactor`). Persisting it per site is deferred.
- [ ] HTML5 fullscreen: `ContainsFullScreenElementChanged` → hide chrome, `WindowState=Maximized`, `WindowStyle=None`, and restore on exit. F11 toggles app fullscreen.
- [ ] Context menu (`ContextMenuRequested`): add "Open link in new tab". Keep the defaults otherwise.
- [ ] Permission requests (`PermissionRequested`): use the default WebView2 prompt for now; a custom prompt and per-site memory come later.
- [ ] Status text on link hover (`StatusBarTextChanged`) in a small bottom-left overlay (a `Popup`, to avoid airspace).
- [ ] Last tab closed → close the window (or open a new blank tab, configurable later).
- [ ] Open `.html`/URLs passed on the command line (later default-browser registration, see §9).

**Files:** `Views/Controls/TabStrip.xaml`, `ViewModels/MainWindowViewModel.cs`, `ViewModels/TabViewModel.cs`, `Browser/BrowserTab.cs`, `Services/ShortcutService.cs` (or `InputBindings` in XAML).
**Packages:** none.
**Risks / edge cases:** memory with many tabs (suspend background tabs later); focus juggling between WPF and the WebView2 HWND; disposal races (events firing after close, so unsubscribe and null-check); `NewWindowRequested` must assign `NewWindow` before navigation starts; popups that call `window.close()` (`WindowCloseRequested` → close the tab).
**Manual verification:** open 10 tabs and switch with the mouse and `Ctrl+Tab`. Close the middle tab → the correct neighbour becomes active. `Ctrl+Shift+T` restores it. Middle-click a link → new tab. "Sign in with Google" on a site → the popup works and closes itself. YouTube video fullscreen works. `Ctrl+L` while the page has focus focuses the address bar. Zoom in/out/reset.
**Automated tests:** closed-tab stack logic and tab-selection-after-close logic (pure functions in Core or testable VM logic).
**Done when:** daily multi-tab browsing feels normal, with no leaked WebView processes after closing tabs (check Task Manager).

---

### Phase 3 — Bookmarks, history, settings and persistence · **M–L**

**Goal / outcome:** Bookmarks, browsing history with search, settings (homepage, search engine, theme, download folder), and session restoration.

**Depends on:** Phase 2.

**Checklist**
- [ ] Add `Microsoft.Data.Sqlite` to Core. `Database` opens with WAL mode and migrations via `PRAGMA user_version`.
- [ ] Schema v1: `history(id, url, title, visited_at, visit_count)`, `bookmarks(id, parent_id, title, url, position, created_at)`, `site_settings(host, adblock_enabled, zoom, …)`, `session_tabs(window_id, position, url, title, is_active)`, `downloads(...)` (used in Phase 4).
- [ ] Repositories with async methods. All DB access runs off the UI thread.
- [ ] `SettingsStore` (`settings.json`, System.Text.Json, atomic write via temp + replace): homepage, startup behaviour (new tab / homepage / restore session), search engine (presets: Bing, Google, DuckDuckGo, Brave Search, plus custom `{query}` template), theme, download folder, ask-where-to-save, adblock defaults.
- [ ] History recording on `NavigationCompleted` (success, top-level, non-private), with duplicate visits within a short window merged.
- [ ] History page: search, group by day, delete entry, clear range (also offer clearing WebView2 browsing data through `Profile.ClearBrowsingDataAsync`).
- [ ] Bookmarks: star button in the address bar (`Ctrl+D`), bookmarks page/manager (folders, rename, delete), optional bookmarks bar.
- [ ] Address bar suggestions from history and bookmarks (simple prefix/contains match, debounced, background query).
- [ ] Internal pages: implement Settings/History/Bookmarks/Downloads as **WPF views shown in a tab** (no web content, so no privileged web bridge needed). A tab can be "web" or "internal".
- [ ] Session restore: save the tab list (debounced) on change and on exit; restore on startup if enabled. Skip private windows.
- [ ] Import from other browsers is deferred.

**Files:** `Core/Persistence/*`, `Core/Settings/*`, `Views/Pages/*`, `ViewModels/HistoryViewModel.cs`, `BookmarksViewModel.cs`, `SettingsViewModel.cs`.
**Packages:** `Microsoft.Data.Sqlite` (local relational storage).
**Risks / edge cases:** DB locked/corrupt → back up and recreate with a user notice; very long URLs/titles; migrations must be forward-only and tested; restore must not open hundreds of tabs eagerly (lazy-load background tabs).
**Manual verification:** visit pages → they appear in History, searchable. Bookmark a page and restart → the bookmark persists. Change the search engine → the address bar uses it. Restart with "restore session" → tabs return. Clear history works.
**Automated tests:** migrations on an empty and an old-version DB (temp file), repository CRUD (in-memory or temp SQLite), settings serialisation round-trip and defaults for missing fields, suggestion ranking.
**Done when:** data survives restarts, the UI never blocks on DB access, and the settings take effect.

---

### Phase 4 — Normal download manager · **L**

**Goal / outcome:** Browser downloads appear in AJBrowser's own download panel and page, with progress, speed, ETA, pause/resume where supported, cancel, retry, open, show in folder, and persistent history.

**Depends on:** Phase 3 (persistence, settings). It also builds the `HttpFileDownloader` that Phase 5 uses.

**Checklist**
- [ ] `DownloadItem` model (Core): `Id`, `Kind` (Browser/App), `State` (Queued, Downloading, Paused, Completed, Failed, Cancelled, Interrupted), `SourcePageUrl`, `DisplayUrl` (redacted), `FilePath`, `TotalBytes?`, `ReceivedBytes`, `StartedAt`, `CompletedAt`, `Error`, `CanPause`, `CanResume`.
- [ ] `DownloadInterceptor` (Browser): `DownloadStarting` → `Handled = true`. Compute the path via `FileNamer` and settings (ask-where-to-save shows `SaveFileDialog` using a deferral). Subscribe to `BytesReceivedChanged`, `StateChanged`, `EstimatedEndTimeChanged`.
- [ ] `DownloadManager` (Core): owns all items, persists state changes, throttles progress (≈4/sec), computes speed (moving average) and ETA (when the total is known).
- [ ] `HttpFileDownloader` (Core): streaming to `.ajpart`, a buffer of ≥ 81 KB, Range resume when supported (`Accept-Ranges`, `ETag`/`Last-Modified` validation via `If-Range`), retry with backoff on transient errors, cancellation, atomic rename. It has no UI dependency and is used for app-managed jobs.
- [ ] Queue and concurrency: app-managed jobs use `SemaphoreSlim` (default 3, configurable). Browser downloads are not queued by us (the runtime controls them); document this.
- [ ] Download panel: a toolbar button with a progress ring badge, and a `Popup` flyout listing recent downloads (it renders above the WebView). "Show all" opens the Downloads page.
- [ ] Downloads page: the full list, search, clear completed, per-item actions.
- [ ] Actions: Open (`ShellExecute` via `Process.Start` with `UseShellExecute=true`), Show in folder (`explorer.exe /select,"path"` via `ArgumentList`), Copy link (redacted for signed URLs), Retry (browser: navigate to the original URL in a tab; app: restart the job), Cancel (deletes the partial file).
- [ ] On startup: mark previously in-progress browser downloads as `Interrupted`, offer resume for app jobs, and clean orphaned `.ajpart` files older than N days (ask first).
- [ ] Settings: default folder, always-ask, max concurrent app downloads.
- [ ] Dangerous file types: rely on WebView2 SmartScreen for browser downloads. App downloads only fetch media types.

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

**Checklist**
- [ ] `MediaSniffer` (Browser): subscribe to `WebResourceResponseReceived` per tab, extract metadata cheaply, and push to a `Channel<MediaObservation>`. Capture the `NavigationId` from `NavigationStarting`.
- [ ] `MediaClassifier` (Core): content type first, extension as a fallback, segment exclusion rules (§6.2). Pure and tested.
- [ ] `MediaDetectionService` (Core): per-`(TabId, NavigationId)` store, dedupe, confirmation logic, events (throttled). Clear on new document, dispose on tab close.
- [ ] `RequestContext` (Core/Security): captures the **allowlisted** request headers from the observed request (`Referer`, `Origin`, `User-Agent`, `Accept-Language`). Cookies are fetched **at download start** from the tab's profile `CookieManager.GetCookiesAsync(mediaUrl)` (cookies the browser would send to that exact URL). Never copy `Authorization` from another origin; never persist the context.
- [ ] Optional hint script (`Scripts/video-hints.js`, injected with `AddScriptToExecuteOnDocumentCreatedAsync`): posts `{currentSrc, videoWidth, videoHeight, duration, isBlob}` for `<video>` elements on `loadedmetadata`. The host validates and treats it as untrusted.
- [ ] UI: a toolbar "Videos detected (n)" button, visible only when n > 0. `Popup` picker: each item shows thumbnail-less info (page title, resolution or "Unknown quality", format, size or "Unknown size", source host), a Download button, and a "Save as…" option.
- [ ] Download → `DownloadManager.EnqueueApp(job)` using `HttpFileDownloader` with the `RequestContext`. Suggested filename: page title + resolution + extension.
- [ ] Expired URL handling: a `403`/`410` on start → "Link expired — replay the video and try again" (and re-detection auto-refreshes the URL).
- [ ] Add "Download video" to the context menu when right-clicking a `<video>` with a non-blob `src`.
- [ ] Settings toggle: enable/disable media detection.

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

### Phase 8 — Ad and tracker blocking · **L**

**Goal / outcome:** Ads and trackers are blocked by default, with a toolbar shield showing the blocked count, a per-site toggle, a global toggle, an allowlist, and automatically updated community filter lists.

**Depends on:** Phase 3 (site settings), the Phase 0.5 decision. Can run in parallel with Phases 5–7 if desired.

**Checklist (assuming the recommended adblock-rust path; adjust per §12)**
- [ ] Native shim: `native/adblock-shim/` Rust crate exposing a C ABI (`engine_new_from_lists`, `engine_serialize/deserialize`, `engine_check(url, source_url, request_type) -> {matched, exception, redirect?}`, `engine_url_cosmetic_resources(url)`, `engine_hidden_class_id_selectors(classes, ids, exceptions)`, `free_*`). Prebuilt `adblock_shim.dll` (x64) committed via LFS or produced by a CI step. Document the build.
- [ ] `IFilterEngine` (Core) + `AdblockRustEngine` (P/Invoke, `SafeHandle`, UTF-8 marshalling, thread-safety rules).
- [ ] `FilterListUpdater` (Core): list catalogue (EasyList, EasyPrivacy, optional uBlock filters / Peter Lowe), conditional GET, `! Expires:` (default 4 days), background update on startup + timer, keep the last good copy, compile to a serialised engine file, hot-swap the engine atomically.
- [ ] `RequestBlocker` (Browser): filter registration with source kinds (worker kinds on one WebView only). Map `CoreWebView2WebResourceContext` → engine request type. Decision → `CreateWebResourceResponse(null, 403?, "Blocked", "")` (or redirect resources later). **No allocations-heavy work in the handler.**
- [ ] `BlockingPolicy` (Core): global toggle, per-site disable (by host, later eTLD+1), and an allowlist UI. The decision is cached per navigation.
- [ ] Cosmetic filtering: on document creation and per frame, inject a domain-specific hide stylesheet. Generic class/id selector script with `MutationObserver`, using batched, debounced messages to the host. Validate messages (untrusted), cap sizes.
- [ ] UI: shield button with a count (per tab, per navigation; the count is batched). The popup shows the count, a "Blocking on this site" toggle (reloads the page), a link to settings.
- [ ] Settings page: global toggle, lists (enable/disable, last updated, update now), allowlist management, tracking prevention level (Balanced/Strict), list attributions.
- [ ] Performance instrumentation (DEBUG): handler time histogram, requests/sec, logged on demand.

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
- [ ] Tabs in the title bar via `WindowChrome` (caption buttons, drag regions, snap layouts on Windows 11, maximised border handling). **Risky, so keep the standard title bar until then.**
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
- [ ] Per-site zoom and permission memory UI.
- [ ] Password manager integration (WebView2 autofill settings exist, but UI parity is limited).
- [ ] Tab tear-off and multi-window drag.
- [ ] Default-browser registration.
- [ ] ARM64 build.
- [ ] Sync (out of scope for a solo project unless a backend is chosen).
- [ ] `WebView2CompositionControl` (a WPF control without airspace limits, at a possible performance cost). **Unverified**; evaluate only if airspace becomes painful.

---

## 10. Items to verify

| # | Question | How | Status |
|---|---|---|---|
| V1 | Does `WebResourceResponseReceived` expose the content type, 206 ranges and the final URL after redirects? | Spike | ☐ |
| V2 | Does `Response.GetContentAsync()` reliably return manifest bodies? | Spike | ☐ |
| V3 | Do browser downloads continue when their WebView/tab closes? | Spike | ☐ |
| V4 | Is `WebResourceRequested` with `"*"` + All source kinds fast enough on heavy sites (UI thread)? | Spike | ☐ |
| V5 | Can an MV3 blocker (uBO Lite) run in WebView2 via `AddBrowserExtensionAsync`, and how well? | Spike | ☐ |
| V6 | Does `NewWindowRequested` + `args.NewWindow` keep `window.opener` for OAuth popups? | Spike | ☐ |
| V7 | Which keys does WebView2 consume vs. forward to WPF (`Ctrl+L`, `Ctrl+T`, `Ctrl+W`, `F5`, …)? Should we set `AreBrowserAcceleratorKeysEnabled`? | Phase 2 | ☐ |
| V8 | Is the WebView2 Find API available and stable in SDK 1.0.4370.26? | Phase 2 | ☐ |
| V9 | How stable is the in-box WPF Fluent `ThemeMode` in .NET 10 for our controls? | Phase 0 | ☐ |
| V10 | Velopack's current status and fit (self-contained WPF, per-user install, updates) | Phase 10 | ☐ |
| V11 | Is the ICoreWebView2_22 three-parameter filter available in the installed SDK and runtime? | Spike | ☐ (docs confirm the API exists) |
| V12 | Exact WPF `EnsureCoreWebView2Async(environment, controllerOptions)` overload for private mode | Phase 1 | ☐ |

**Verified from primary docs (2026-11-26):** `AddWebResourceRequestedFilter` with `CoreWebView2WebResourceRequestSourceKinds` (Document / SharedWorker / ServiceWorker; register worker kinds on only one WebView per environment); `AreBrowserExtensionsEnabled` + `AddBrowserExtensionAsync` (unpacked extension folder; extension UI entry points are missing in WebView2); WPF `ThemeMode` exists and is marked experimental (WPF0001). No maintained .NET binding for adblock-rust was found (its official bindings are Rust and JS, with community Python bindings).

---

## 11. Spike findings log

_Fill in during Phase 0.5. Record the WebView2 runtime version for each finding._

| Date | Runtime version | Question | Finding |
|---|---|---|---|
| | | | |

---

## 12. Decision log

| Date | Decision | Reason |
|---|---|---|
| 2026-11-26 | Keep .NET 10 / WPF / WebView2 Evergreen / CommunityToolkit.Mvvm / Microsoft.Data.Sqlite | Matches the existing project and the requirements |
| 2026-11-26 | Two projects (App + Core) plus a test project | Testability without over-architecture |
| 2026-11-26 | No EF Core, DI container or logging framework initially | Minimise dependencies; revisit if needed |
| 2026-11-26 | FFmpeg as an external LGPL process, introduced in Phase 6 | Remux only; license-friendly; not needed for direct files |
| 2026-11-26 | App-managed HLS/DASH segment download (not FFmpeg network input) | Keeps cookies in-process, precise progress, resumable |
| _pending_ | Ad-block engine: adblock-rust shim vs. extension vs. other | Decide after Phase 0.5 |
| _pending_ | Theme: in-box Fluent `ThemeMode` vs. custom vs. WPF-UI | Decide in Phase 0 |
| _pending_ | Installer/updater | Decide in Phase 10 |

---

## 13. How to continue with the coding agent

1. **Start each session** by asking the agent to read `IMPLEMENTATION_PLAN.md` (this file) and the current code. Name the phase or task, for example: *"Implement Phase 1, items 1–4."*
2. **One phase (or a slice of one) per session.** Don't skip ahead. Later phases assume earlier checklists are done.
3. **The agent must update this file** in the same change as the code:
   - tick completed checkboxes (`- [x]`);
   - update **Current phase** at the top;
   - add spike results to §11 and decisions to §12;
   - move anything discovered to §9 (deferred) or §10 (verify) rather than silently expanding scope.
4. **Verification is explicit:** the agent states what it built, what it ran (build, tests), and what you must check manually in Visual Studio. It never claims to have run the app unless it did.
5. **Before a new dependency** is added, the agent explains why and updates §3. New packages need a reason.
6. **Commit per completed slice** with a message referencing the phase (for example, `Phase 1: address bar and navigation commands`).
7. If reality contradicts the plan (an API doesn't behave as documented), **update the plan first**, then the code.
