# AJBrowser — Progress & Session Handoff

> **For Claude (or any coding agent) starting a new session:** read this file first, then
> `IMPLEMENTATION_PLAN.md` (the full roadmap). This file is the short "where are we" summary;
> the plan is the source of truth for scope, architecture, checklists and findings.

**Last updated:** 2026-11-27 · **Current phase:** Phase 0 ✅ · Phase 0.5 ✅ · Phases 1, 2, 2.5 and 3 🔶 built, awaiting your manual checks · **Phase 4 🔶 built · Phase 5 🔶 built · Phase 6 (HLS) 🔶 built (real public stream downloaded by the agent) · Phase 7 (DASH) 🔶 built (7a–7c, checked in the real app with your FFmpeg) · Phase 7.5 (yt-dlp) 🔶 built (YouTube downloaded from the picker by the agent) · Phase 8 (own ad-block engine) 🔶 in progress: 8a + 8b + 8c done (8c: in the browser, with the engine switch in Settings) · next: 8d**

---

## 1. What this project is

AJBrowser is a Windows desktop browser built with **C# / WPF / .NET 10** on **Microsoft WebView2** (Evergreen). Planned features:
- everyday browsing with tabs, bookmarks and history
- a built-in download manager
- video detection with a quality picker (direct MP4/WebM, then HLS, then DASH)
- built-in ad and tracker blocking

There's no DRM circumvention, and we don't promise support for every site.

**About the developer (the user):** new to C#/WPF, experienced with React, JavaScript, Flutter and web development. Explain WPF concepts with web/React analogies where it helps, and keep the architecture simple (solo developer).

## 2. Working rules for the agent

1. **Never run `git commit` (or push).** The user reviews and commits themselves. Leave changes in the working tree and list what changed.
2. **Work phase by phase** from `IMPLEMENTATION_PLAN.md`. Don't skip ahead or silently expand scope; put new ideas in plan §9 (deferred) or §10 (to verify).
3. **Update the docs in the same change as the code:**
   - in `IMPLEMENTATION_PLAN.md`: tick checkboxes and update "Current phase", §11 findings and §12 decisions;
   - in this file: update "Current state", "Next task" and "Last updated".
4. **Be honest about verification:** state what was built, built successfully, tested and launched, and list the manual checks the user should do in Visual Studio. Never claim something ran if it didn't.
5. **Explain before adding any new NuGet package or tool**, and record the reason in plan §3.
6. **Keep the plan a local file.** Don't publish `IMPLEMENTATION_PLAN.md` or this file as a claude.ai artifact.
7. **Security basics** (plan §5.8): never log cookies, auth headers or signed URLs (use `Redactor`); no host objects exposed to web pages; treat web messages as untrusted; never disable certificate validation.

## 3. Setting up on a new PC

1. **Install:**
   - Visual Studio 2026 with the **.NET desktop development** workload
   - a .NET SDK that can build `net10.0` (.NET 10 or newer; the first PC used SDK 11.0.100)
   - the .NET 10 Desktop Runtime (VS usually installs it)
   - the Microsoft Edge WebView2 Runtime (preinstalled on Windows 11)
2. **Get the code:** `git clone` / `git pull`, then open `AJBrowser.slnx`.
3. **Check it works:**
   - Build (Ctrl+Shift+B): expect 0 warnings and 0 errors.
   - Test Explorer: expect **922 passing tests**.
   - Rust is **not** needed to build: the ad-block engine DLL is committed (`AJBrowser.Core/Native/adblock_shim.dll`). Only to change `native/adblock-shim/` install Rust (GNU toolchain) as its README says.
   - F5: the browser window opens with one tab and loads the Bing home page, with a bookmarks bar under the toolbar. In the address bar is a ☆ star; left of **⋯** are the downloads button (↓) and the ad-block shield (green = blocking). The **⋯** menu has History, Bookmarks, Downloads, Settings, Theme, Open data folder, and the Debug-only **Diagnostics** / **Throw test exception** items.
   - On the very first run on a PC, the ad blocker installs itself (the first page waits about 4 s).
   - Settings → Privacy → **Ad-block engine** picks uBlock Origin Lite (default) or AJBrowser's own engine; the first switch to AJBrowser's engine downloads its filter lists (about 3 s).
   - Debug builds only: set the environment variable `AJBROWSER_DATA_DIR` to a folder to run a test copy on separate data (settings, history, WebView2 profile) next to your normal one.
   - Make sure **AJBrowser** (not AJBrowser.Core.Tests) is the startup project. Running the test project just prints "exited with code 0".
4. **Per-PC data:** the app stores data in `%LOCALAPPDATA%\AJBrowser\` (`Data\ajbrowser.db` = history, bookmarks, session; `Data\settings.json`; logs, WebView2 profile, unpacked ad blocker, spike reports). That folder isn't in git, so a new PC starts empty, which is expected.

**CLI equivalents** (run in the repo root):
```powershell
dotnet build AJBrowser.slnx
dotnet test AJBrowser.Core.Tests
dotnet run --project AJBrowser
```

## 4. Current state of the code

```
AJBrowser.slnx
Directory.Build.props        shared: Nullable, ImplicitUsings, Product, Version 0.1.0
README.md                    build/run + data folders
THIRD-PARTY-NOTICES.md       uBO Lite (GPLv3, version, SHA-256, source link), WebView2, CommunityToolkit; copied to output
IMPLEMENTATION_PLAN.md       full roadmap (phases 0–10), findings, decisions
PROGRESS.md                  this file
AJBrowser/                   WPF app (net10.0-windows)
  App.xaml(.cs)              OnStartup (async): services, WebView2 runtime check, corrupt-DB notice, bookmarks load,
                             session restore + command-line URLs, global exception handlers, --diagnostics args (DEBUG)
  AppServices.cs             hand-written composition root (no DI container): loads settings + opens DB before the window,
                             CreateTab(), CreatePage() for internal pages, applies theme/search engine on settings change
  Browser/BrowserEnvironment.cs  shared CoreWebView2Environment (lazy, recreatable), runtime check, profile "Default",
                             AreBrowserExtensionsEnabled = true
  Browser/ExtensionManager.cs    ad blocker: unpack bundled uBO Lite zip → install/upgrade in profile once per start,
                             (8c) enabled only while AdBlockService says it should run; settings page URL
  Browser/AdBlockService.cs  (8c) which ad blocker runs (setting) + global switch; starts AJBrowser's engine on demand
                             (saved engine, list downloads, hourly checks); every new WebView awaits PrepareAsync first
  Browser/RequestBlocker.cs  (8c) per tab: request filter while AJBrowser's engine is active, block/redirect, restarts the
                             first visit to a site so its scripts are in place (plan F29)
  Browser/CosmeticInjector.cs (8c) per tab: per-host scriptlet + hiding scripts (last 24 hosts), generic-hiding messages
  Extensions/uBOLite_2026.1124.1811.chromium.zip  official release, unmodified (GPLv3), copied to output
  Browser/BrowserTab.cs      wraps one WebView2: Configure() = ALL per-WebView setup, navigation, crash recovery,
                             popups, favicon, zoom, find, fullscreen, context menu
  Browser/DownloadInterceptor.cs  (4b) DownloadStarting → Handled, file name/folder (or Save dialog), registers with
                             DownloadManager, forwards progress; pause/resume/cancel of live browser downloads;
                             (4c) knows which WebView owns which download and keeps closed tabs' WebViews (Park) until done
  Browser/MediaSniffer.cs    (5b) per tab: watches responses (+ a Media request filter), hint script, TabMediaStore,
                             throttled MediaChanged
  Browser/PopupRequest.cs    hands a new tab's WebView to WebView2 as a popup (deferral + NewWindow)
  Browser/PageProblem.cs     error/crash panel model + which navigation errors get a panel
  Services/ThemeService.cs   Fluent ThemeMode (WPF0001 suppressed here) + Light/Dark brush swap, follows Windows
  Services/ShellService.cs   open folder, open file, show file in folder (SHOpenFolderAndSelectItems), open URL, Notepad
  Services/KnownFolders.cs   the Windows Downloads folder (also when moved)
  Services/MediaDownloadService.cs (5b) detected video → app download (name, folder, allowlisted headers + cookies)
  Services/ToolService.cs    (7.5b) which yt-dlp/FFmpeg/Deno to use, install/update, status lines, daily yt-dlp update
  ViewModels/Pages/ToolRowViewModel.cs (7.5b) one tool row in Settings → Downloads
  Services/YtDlpService.cs   (7.5c) the picker's yt-dlp side (on/off, read a page, quality rows, start, install a tool)
  ViewModels/YtDlpVideoViewModel.cs (7.5c) the yt-dlp row in the Videos picker
  Services/HistoryService.cs record visits/titles (queued background writes), search, delete, clear range
  Services/BookmarkService.cs all bookmarks in memory + write-through to SQLite, folder choices, Changed event
  Services/SessionService.cs debounced (1 s) session saves + final synchronous save at exit
  Services/FaviconService.cs site icons per host (in memory + SQLite) for History, Bookmarks, bar, suggestions
  ViewModels/MainWindowViewModel.cs   tab list, selected tab, close/reopen, popups → tabs, fullscreen, theme (via settings),
                             startup/session restore, bookmarks bar, internal pages, session snapshot
  ViewModels/TabViewModel.cs address bar, title, favicon, loading, back/forward, zoom, find, blocked popups, problem panel,
                             internal page or web page, lazy engine start, history recording, star, suggestions
  ViewModels/DetectedMediaViewModel.cs (5b) one row in the Videos picker
  ViewModels/DownloadsViewModel.cs    (4b) download list for button ring, flyout and page; routes actions
  ViewModels/DownloadItemViewModel.cs (4b) one download row (status text, progress, which buttons apply)
  ViewModels/AdBlockViewModel.cs      ad-block state shared by the shield and the Settings page
  ViewModels/SuggestionsViewModel.cs  address-bar suggestions (debounced, ranked in Core)
  ViewModels/BookmarkEditorViewModel.cs  the star's edit panel
  ViewModels/InternalPageContracts.cs  IInternalPage, IPageHost, OpenTarget, TabServices
  ViewModels/Pages/          History/Bookmarks/Settings/DownloadsPageViewModel (ajbrowser:// pages)
  Views/MainWindow.xaml(.cs) no system title bar (WindowChrome; own caption buttons, snap-layout hook), tab strip, toolbar, ⋯ menu, bookmarks bar, find bar, one host per tab (web or internal page),
                             problem panel; code-behind: keyboard shortcuts, fullscreen, menus, bookmarks-bar clicks
  Views/Controls/            AddressBar (star + edit panel, suggestions drop-down), TabStrip (+ ShrinkingTabPanel), FindBar,
                             SiteIcon (favicon or fallback glyph), DownloadPanel (downloads flyout), ProgressArc (button ring),
                             VideoPicker (Videos on this page)
  Views/Pages/               HistoryPage, BookmarksPage, SettingsPage, DownloadsPage (WPF views shown in a tab)
  Themes/                    Light.xaml, Dark.xaml (semantic brushes), Shared.xaml (fonts, sizes, ToolbarButtonStyle,
                             SwitchToggleStyle), Pages.xaml (internal page styles)
  Diagnostics/               Phase 0.5 spike, DEBUG ONLY (excluded from Release via csproj)
native/adblock-shim/         (8a) Rust: C wrapper around Brave's adblock-rust 0.13.6; build.ps1 → AJBrowser.Core/Native/adblock_shim.dll
tools/ubo-resources/         (8a) generate.mjs: uBO release → scriptlet/redirect resources for adblock-rust
AJBrowser/Filters/ubo-resources.json  (8a) its output from uBO 1.77.4 (GPLv3), copied to the app's output
AJBrowser.Core/              UI-free logic (net10.0), TreatWarningsAsErrors
  Native/adblock_shim.dll    (8a) the built engine (committed; copied next to the app and tests)
  Blocking/                  (8a) IFilterEngine, AdblockRustEngine (+ NativeMethods, EngineHandle), RequestKind,
                             FilterListText, FilterDecision, CosmeticResources;
                             (8b) FilterListCatalog (uBO's default lists + mirrors), FilterListPreprocessor (!#if/!#include,
                             Expires), FilterListDownloader (conditional GET, raw cache, includes), FilterEngineService
                             (saved engine, background updates, atomic swap), BlockingPolicy (global + per-site off);
                             (8c) CosmeticScripts (page scripts + message checks), RedirectResource
  Persistence/SiteSettingsRepository.cs (8b) sites with ad blocking off; schema v7
  Infrastructure/AppPaths.cs %LOCALAPPDATA%\AJBrowser\{WebView2,Data,Filters,Extensions,Logs,Temp,Tools}
  AdBlocking/                ExtensionVersion, ExtensionPackage (read zip manifest, unpack, clean old),
                             AdBlockerInstallPlanner (keep/install/replace), AdBlockerState (state.json)
  Logging/                   LogEntry, ILogSink, AppLogger (always redacts), FileLogSink (daily, 5 MB roll, keep 10)
  Security/Redactor.cs       strips URL query/fragment/user-info; redacts Cookie/Authorization/Bearer in text
  Navigation/                UrlOrSearchResolver (search template changeable), UrlDisplay, ZoomLevels, FindStatus,
                             CommandLineUrls, InternalPages (ajbrowser://), HistoryRules, SuggestionRanker
  Persistence/               Database (WAL, migrations via user_version, corrupt-file recovery), Migrations (v1),
                             History/Bookmark/Session/Favicon/DownloadRepository (async, SQL off the UI thread); schema v3
  Settings/                  AppSettings (record, defaults, Normalize), SettingsStore (settings.json), SearchEngines
  Downloads/                 (Phase 4a) DownloadItem + state machine, FileNamer, TransferRateEstimator,
                             HttpFileDownloader (.ajpart, resume, retry), DownloadManager (queue, pause/resume/cancel, saving);
                             (4b) DownloadText (sizes, speed, time left, status line), BrowserDownloadStatus (WebView2 states);
                             (4c) PartialFileCleanup (old .ajpart files)
  Media/                     (5a) MediaClassifier (direct/HLS/DASH/segment), TabMediaStore (per-tab list, dedupe,
                             confirmation), VideoHint (strict parsing of the page script's message), MediaFileNamer
  Media/Hls/                 (6a) HlsParser (master + media playlists), HlsAnalysis (DRM/live, codecs, container,
                             labels, size estimate), HlsDecryptor (AES-128); (6b) HlsDownloader (pieces in parallel,
                             retries, resume, decryption, assembly; cookies only to their own host)
  Media/Dash/                (7a) DashManifest (tracks + pieces), DashParser (BaseURL chain, SegmentTemplate/Timeline/
                             List/Base, ISO-8601 durations, template expansion), DashAnalysis (DRM/live/multi-period,
                             best-first, audio choice, container, labels); (7b) DashDownloader (inspect, prepare, download
                             via the shared HLS piece engine; whole files in 8 MB ranges)
  Media/StreamDownloaders.cs (7b) routes HLS / DASH jobs for the DownloadManager
  Media/YtDlp/               (7.5a) YtDlpInfo (+parser of -J JSON), YtDlpFormats (quality rows, sound choice), YtDlpArguments,
                             YtDlpProgress (tracker), YtDlpErrors (plain sentences + fix), NetscapeCookieFile, YtDlpCookieScope;
                             (7.5b) YtDlpDownloader (inspect + download jobs, cookies only after "sign in", pause/resume/cancel);
                             (7.5c) YtDlpSites (which pages get the film button)
  Tools/                     (7.5a) ToolCatalog (yt-dlp, FFmpeg LGPL, Deno), GitHubRelease + Checksums, ToolInstaller (find,
                             verified install/update, daily check), ProcessRunner, ChildProcessJob (tools end with AJBrowser)
  Media/Ffmpeg/              (6b) FfmpegLocator (Settings path or PATH), FfmpegRunner (local files only), progress parser
  Security/RequestContext.cs (5a) allowlisted headers for app downloads; cookies only added at download time
  Tabs/                      ClosedTabStack, TabSelection
AJBrowser.Core.Tests/        xUnit: Redactor, AppPaths, logging, navigation, tabs, ad blocking, persistence, settings, favicons, downloads, media detection, HLS, DASH, yt-dlp, tools, filter engine + shipped uBO resources, filter lists, blocking policy (881 tests)
```

**Packages:**
- App: `Microsoft.Web.WebView2` 1.0.4370.26, `CommunityToolkit.Mvvm` 8.4.2
- Core: `Microsoft.Data.Sqlite` 11.0.0 (added in Phase 3)
- Tests: xUnit 2.9.3, `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1
- Native (no NuGet): `adblock_shim.dll` = adblock-rust 0.13.6 (MPL-2.0), built with Rust 1.100.0 (GNU)

## 5. Phase status

| Phase | Status | Notes |
|---|---|---|
| 0 Foundation | ✅ Done | User verified and committed ("phase 0 done") |
| 0.5 Feasibility spike | ✅ Done | Automated suite, cookie test and uBO Lite extension test (V5 → F15: blocks YouTube ads) done. Ad-block engine decided. |
| 1 Single-tab browser | 🔶 Built | Agent: build, 113 tests, launch + home page load. **You:** the manual checks in §6. |
| 2 Tabs | 🔶 Built | Agent: build, 166 tests, launch with 2 startup tabs + script popup blocked. **You:** the manual checks in §6. |
| 2.5 Ad blocking (uBO Lite) | 🔶 Built | Agent: build, 205 tests, launches (installs on first start, reused on the next, off/on choice kept), toolbar shield + switch driven via UI Automation. **You:** ad blocking confirmed working; remaining checks in §6. |
| 3 Bookmarks, history, settings | 🔶 Built | Agent: build, 298 tests, sharp site icons in lists and tabs, app driven via UI Automation (star, history, bookmarks, settings, suggestions, session restore of 4 tabs, clear history). **You:** the checks in §6. |
| 4 Download manager | 🔶 Built | **4a** Core engine; **4b** interceptor, toolbar button + flyout, Downloads page, actions; **4c** downloads survive closing their tab (keep-alive verified in a standalone spike, F18), blank download tabs close themselves, exit question, old `.ajpart` cleanup, "Simultaneous video downloads" setting. 399 tests, build 0 warnings. The agent did **not** launch AJBrowser itself (it would write into your real data folder). **You:** the Phase 4 checks in §6. |
| 5 Video detection | 🔶 Built | **5a** Core detection logic. **5b** checked by the agent in the real app (real &lt;video&gt; found and downloaded byte-identical with Referer + cookie; size rule; HLS = one entry; clears on a new page); real file names after your report. **5c** right-click "Download video", Settings switch, Save as…, expired-link message + fresh link on Retry, V14 decided. 488 tests. **Pending:** the agent's real-app check of 5c (needs AJBrowser closed), and your quick try. |
| 6 HLS streams | 🔶 Built | **6a** Core HLS logic; **6b** download engine (checked on a local stream); **6c** quality list with size estimates, one row per video, separate sound merged when FFmpeg is set. **Agent check with real public streams:** Apple's sample listed its 24 qualities; Mux's "Big Buck Bunny" downloaded at 184p into a valid H.264/AAC `.ts`. 555 tests. **You:** try a site; with FFmpeg set, check that sound gets merged (not tested by the agent, no FFmpeg here). |
| 7 DASH | 🔶 In progress | **7a** Core: manifest parsing (all four ways DASH lists pieces), DRM/live/multi-period refusal, audio-track choice, container, labels. **7b** download engine (shared with HLS; DB migration v6); agent check on real public streams: 5 manifests read correctly, Big Buck Bunny 180p and a VP9 whole-file track downloaded into valid MP4/WebM files. Also fixed: a deleted FFmpeg made stream downloads fail instead of saving the picture. **7c** picker: quality list + sound-track list for DASH rows, merge note. **Agent check in the real app with your FFmpeg 9.1:** a public DASH stream listed 10 qualities and 10 sound tracks; VP9 + automatic sound → WebM with English Opus, H.264 + automatic → MP4 with English AAC, H.264 + German chosen → MP4 with German sound (all checked with ffprobe). 644 tests. **You:** try a site with a DASH player. |
| 7.5 yt-dlp | 🔶 Built | **7.5a** Core (installer, quality rows, arguments, progress, errors, cookie file). **7.5b** download engine + Settings (switch with notice, auto-update, Install/Update/Choose for yt-dlp, Deno, FFmpeg). **7.5c** the film button on YouTube, Facebook, Instagram, TikTok, X, Vimeo, Reddit, Twitch, Dailymotion, Bilibili video pages, the yt-dlp row (qualities, sound, fix buttons), "Try yt-dlp on this page", right-click entry. **Agent check in the real app on YouTube:** installed yt-dlp and Deno from the picker, listed 2160p…144p, downloaded 144p with sound; killed AJBrowser mid-download → no yt-dlp left (V17); resumed after restart → finished. Found and fixed on the way: cookies broke public YouTube videos (now only sent when a site asks for sign-in), and older code logged tokens in download addresses. 772 tests. **You:** the Phase 7.5c quick try below (other sites, sign-in, sound languages). |
| 8 Own ad-block engine | 🔶 In progress | Your choices: Rust GNU toolchain, keep uBO Lite **and** the new engine, switchable; F16 confirmed caused by uBO Lite. **8a** done: Rust add-on around Brave's adblock-rust, C# wrapper, uBO scriptlet file (generated with your permission), 33 tests (820 total). Measured on real lists: builds in 0.45 s, loads from its saved file in 4 ms, p99 under 40 µs per request; YouTube gets 32 scriptlet calls. **8b** done: uBO's default list set with mirrors, `!#if`/`!#include` handling, conditional downloads, saved engine, background updates with last-good fallback, per-site off (DB migration v7), 61 tests (881 total). Checked against the real servers: first download + build 2.6 s, restart loads in 43 ms without network. **8c** done: request blocking, scriptlets, element hiding, Settings → "Ad-block engine" (uBO Lite / AJBrowser), one switch for both; 41 tests (922 total). Checked by the agent in a separate test copy: YouTube's ad data removed by the scriptlets on the first visit, news sites get their hiding rules, switch on/off works. Next: 8d UI (count, per-site, lists). |
| 9–10 | Not started | |

**Key spike findings to remember** (full table in plan §11, F1–F14):
- Media responses expose content type, length and range, and manifest bodies are readable. But content types are unreliable, DASH segments look like `video/mp4`, and fMP4 HLS uses a byte-range `main.mp4`, so suppress anything a manifest references.
- Request interception is fast enough (about 25–70 µs per request), and service-worker requests are covered.
- Tracking prevention Strict greatly reduces tracker requests.
- **Closing a WebView freezes its downloads.** Keep the owning WebView alive until they finish (Phase 4).
- A navigation that becomes a download completes with `ConnectionAborted`. Don't show an error page for it (Phase 1).
- New-window WebViews **don't inherit** per-WebView setup, so use one shared `BrowserTab` initialisation. WebView2 does **not** block script popups; `IsUserInitiated` distinguishes clicked popups from script popups (Phase 2).
- `CookieManager` returns HttpOnly cookies (GitHub: 12 of 18).
- **uBlock Origin Lite runs in WebView2 and blocks YouTube video ads** (Optimal mode). It's stored as an unpacked extension that references its original folder, and its rules only update with new extension versions (F15).
- CDNs put tokens in URL **paths**, so a `Redactor.MediaUrl` is needed (Phase 5).

**Decided:** ad blocking = uBlock Origin Lite via WebView2's extension API, built in Phase 2.5 and shipped as the official release zip in git (unpacked at first run); adblock-rust + scriptlets (Brave's approach) is an optional Phase 8 upgrade (plan §12).

**Open decisions:**
- **Theme:** the in-box Fluent `ThemeMode` is tentatively accepted. Revisit if visual problems appear.
- **Installer/updater:** Phase 10.

## 6. Next task: Phase 8d (and your quick try of 8c)

**8c done:** AJBrowser's own engine now works in the browser. Choose it in **Settings → Privacy → Ad-block engine → "AJBrowser's engine"** (uBlock Origin Lite stays the default; only one runs at a time, uBO Lite is just switched off, not removed).

**Your quick try of 8c** (the agent checked 1–3 in a separate test copy, not in your normal AJBrowser):
1. Settings → Privacy → pick **AJBrowser's engine** → the line below says "Downloading filter lists…", then e.g. "8 filter lists, 176,282 rules, updated …". The shield stays green.
2. **YouTube:** play several videos (also after typing a new youtube.com address): no ads, and please watch whether **"Experiencing interruptions?" (F16) is gone**.
3. A news site (cnn.com, theverge.com…): ad boxes gone, page works. Shield → Block ads off → the page reloads with ads; on → gone again.
4. **New with 8c, please check:** "Sign in with Google" popups and links that open a new tab still work with AJBrowser's engine (the first visit to a site is restarted a few ms after it begins, plan F29; you shouldn't notice anything).
5. Switch back to **uBlock Origin Lite** → still blocks (its "Ad blocker settings" link works again).
6. If a site breaks only with AJBrowser's engine, tell the agent the address (per-site off comes with 8d).

Known 8c limits: the first time a site appears *inside a frame* it gets no scriptlets (the next page has them); uBO's "procedural" hiding rules (`:has-text()`…) aren't applied yet; open tabs only switch engines after a reload.

**8d next:** shield with the blocked count, "Blocking on this site" toggle, Settings: filter lists (on/off, last updated, Update now), allowlist, tracking prevention level, list credits; DEBUG timing histogram; F16 check.

### Done in 8a

**Scriptlet file done (with your permission):** `AJBrowser/Filters/ubo-resources.json`, generated from uBlock Origin 1.77.4 (99 scriptlets, 54 helpers, 48 redirect files). With the real lists a YouTube page gets 32 ad-removing scriptlet calls. **To update it later** (e.g. when YouTube ads come back and uBO has released a fix): download the newer `uBlock0_<version>.chromium.zip` from github.com/gorhill/uBlock/releases, check its SHA-256, unpack it, run
`node tools/ubo-resources/generate.mjs <unpacked uBlock0.chromium folder> AJBrowser/Filters/ubo-resources.json`,
run the tests, and update the version and SHA-256s in `THIRD-PARTY-NOTICES.md`.


## 6x. Still open from Phase 7.5 (yt-dlp) and earlier checks

**Phase 7.5c quick try** (the agent did 1–3 on YouTube; the rest is new to try):
1. Settings → Downloads: turn on **Big video sites (yt-dlp)** (accept the notice). Installing yt-dlp/Deno there first is optional: the picker offers it too.
2. Open a YouTube video → the film button shows "1" (tooltip "Download this video (yt-dlp)") → click it → the row reads the video ("reading video info…", a few seconds), then title, length, channel and a quality list. If it says yt-dlp/Deno/FFmpeg is missing, click the button in the row.
3. Pick a quality → **Download** → it appears in the downloads list with progress; pause/resume work; the result plays with sound.
4. **Please try:** a public Facebook video, an Instagram reel, a TikTok, a Vimeo video; a YouTube video with several sound languages (a sound list appears); an age-restricted video while signed in to YouTube in AJBrowser (it should work; signed out it should say "needs you to be signed in").
5. On another site with a video: film button → **Try yt-dlp on this page**, or right-click the page → **Download video with yt-dlp**.
6. On YouTube, click another video in the page (no full reload) and reopen the film button → it shows the new video.

**Fixed after your report:** yt-dlp downloads now show a total size from the start (the size the picker showed, replaced by the exact one once yt-dlp knows it) and "Starting…" instead of "0 bytes" while yt-dlp prepares. Please check it once.

**Fixed after your report:** downloads whose title isn't in Latin letters (your Urdu Facebook video) ended with "yt-dlp finished, but the file wasn't found" although the file was complete. Fixed (yt-dlp is told to write UTF-8; AJBrowser also finds the file by its name). That video is already in your Downloads folder; its failed entry can be removed, or Retry marks it complete.

**Fixed after your report:** Instagram rows had no sizes (Instagram tells yt-dlp neither sizes nor the length). AJBrowser now asks Instagram's server for each file's size (about a second), so rows read e.g. "720p · VP9 · MKV · 25.3 MB" and the download shows its total from the start. Without FFmpeg you now also get "Best available · MP4" instead of nothing.

**Fixed after your report:** on pages that aren't yt-dlp sites the picker showed an empty box with empty lists and a false "Added to downloads" above the real rows; it's gone. A stream with only one quality now says so (its quality, or "Single quality: this page only loaded one") instead of showing no list.

**Full download test run (agent, 2026-11-27):** browser downloads, direct video, HLS (local, Mux, Apple), DASH, yt-dlp (YouTube with pause/resume/cancel, Instagram, Facebook, Try yt-dlp) all work in the real app. Fixed on the way: HLS qualities that share a picture playlist (the wrong sound track was downloaded), yt-dlp totals stuck at a too-high estimate, single-quality yt-dlp rows without a label. **Still open:** streams whose playlist links work only once (your phncdn site: "HTTP 410"); fix = read the playlist the browser already received instead of asking again. Details: plan, Checkpoint 7.5c.

**Age-restricted YouTube video (open):** you were signed in, yet it said "needs you to be signed in". The message now tells whether AJBrowser found your sign-in and the site still refused, and the log records yt-dlp's own reason. Try the video again and send the line starting with `yt-dlp couldn't read` from the log (⋯ → Open data folder → `Logs`).

**Old log entries:** before this fix, normal downloads from file hosts wrote their full address (with sign-in tokens in the path) into AJBrowser's log. New entries are redacted; you may want to delete the old log files (⋯ → Open data folder → `Logs`). They never left your PC.

**Phase 7.5b quick try** (Settings → Downloads; the agent ran it once and undid it):
1. Turn on **Big video sites (yt-dlp)** → a notice appears and the switch stays off; **I understand, turn it on** → on, and "Update yt-dlp automatically" appears.
2. **Video tools:** yt-dlp and Deno say "Not installed…", FFmpeg says "Your copy: D:\tools\ffmpeg\bin\ffmpeg.exe". Click **Install** on yt-dlp (≈ 24 MB) and Deno (≈ 43 MB): progress shows in the row, then "Installed by AJBrowser: version …". They go to `%LOCALAPPDATA%\AJBrowser\Tools\`.
3. Optional: **Choose…** your own copy of a tool, then **Clear** to go back.

**Phase 7 quick try** (the agent already ran these on Shaka's public test stream, see plan Checkpoint 7c):
1. A page with a DASH player (for example the dash.js reference player, `https://reference.dashif.org/dash.js/latest/samples/dash-if-reference-player/index.html`, press Load) → the film button shows a "Stream (DASH)" row with its length, a quality list and, when there are several, a sound list.
2. Pick a small quality → Download → it appears in the downloads; the result plays with sound (your FFmpeg merges picture and sound; the note in the row says so).
3. Optional: Settings → Downloads → Video tools → FFmpeg → Clear, reopen the picker → the row says the video will be picture-only; set FFmpeg again afterwards.
4. Protected (DRM) DASH, e.g. on paid streaming sites → "Protected (DRM): can't be downloaded".

Phase 7 added database migration v6 (two columns in `downloads`); it ran by itself. The Phase 6 checks below are also still open.


**Download-complete notice (added after your report; not yet tried in the app):**
- A download finishing while AJBrowser is in front → a small "✓ Download complete" message under the downloads button with **Open** / **Show in folder** (closes by itself after ~7 s); a green dot stays on the button until you open the list.
- While downloads run, the taskbar button shows a progress bar. If one finishes while AJBrowser is in the background or minimized, the taskbar button flashes until you come back.

**Phase 6c quick try:**
5. On a page with an HLS video, open the film button → the stream row says "reading qualities…", then shows the length and a quality list with estimated sizes; pick a small one → Download.
6. With FFmpeg set (Settings → Downloads → FFmpeg), a stream whose sound is a separate track gets it merged into an `.mp4`; without FFmpeg the row tells you and only the picture is saved. *The agent couldn't test merging (no FFmpeg on this PC): please try it once.*

**Phase 6b quick try:**

**Phase 6b quick try** (the agent tested it on a local fake stream):
1. A page that plays an HLS video (many news and video sites) → the film button shows "Stream (HLS) · best quality" → **Download** → it appears in the downloads with progress; pause/resume work. (Opening a `.m3u8` address directly in a tab isn't a page with a player, so it doesn't count.)
2. Without FFmpeg the result is a `.ts` file (opens in VLC, MPC-HC, PotPlayer). To get `.mp4`: install FFmpeg (an "essentials" or LGPL build) and pick `ffmpeg.exe` in Settings → Downloads → FFmpeg.
3. Protected (DRM) and live streams say so in the picker instead of downloading.
4. Note: some streams keep the sound in a separate track; 6b then saves the picture only (the picker says so). 6c merges it when FFmpeg is set.

## 6a. Phase 5 checks (5b + 5c)

**Phase 5c quick try:**
4. Right-click a playing video on a page with a plain video file → "Download video with AJBrowser" (first item) → it appears in the downloads. *Fixed after your report (WebView2 quirk F20) and checked by the agent in the real app. It doesn't appear on streaming players (`blob:` sources) or where the site covers the video with its own player layer.*
5. In the Videos picker, the small save icon next to Download → a Save dialog; Cancel → nothing happens.
6. Settings → Downloads → turn off "Detect videos on pages" → the film button disappears; turn it on and reload → it comes back.
7. Expired link: hard to provoke on purpose; if a video download fails with "The link has expired…", play the video again in its tab and press Retry → it continues.

**Phase 5b quick try** (the agent already ran it on test pages):
1. Open a page with a plain video file, for example a site's own `.mp4` (many news or documentation sites, or `https://www.w3schools.com/html/html5_video.asp`) and let it play → a blue film icon with a number appears left of the address-bar indicators. Click it → "Videos on this page" with quality, size and length → **Download** → it appears in the downloads flyout and plays afterwards.
2. YouTube and most big streaming sites use streams (HLS/DASH) or protected video: you'll see "Stream · HLS/DASH … Soon" or nothing. That's expected until Phases 6–7 (and DRM-protected video is never supported).
3. Go to another page → the film icon disappears.

## 6a. Phase 4 checks (4b + 4c)

**Phase 4b checks** (F5, Debug). Not run by the agent, so this is the first real test. A good test file: any large public test download (for example a speed-test site's 100 MB file).
1. **Download from a normal link** (same tab): Edge's own download bubble does **not** appear; instead AJBrowser's flyout opens under the ↓ button with the file, a progress bar and "x MB of y MB · speed · time left". The ↓ button shows a blue ring filling up while it runs. The file lands in your Downloads folder (or the folder from Settings) with its proper name; downloading it again gives `name (1).ext`.
2. **Pause / Resume / Cancel** in the flyout or on the page: every running download has a Pause button (fixed after your report: it was missing). Pause → "Paused · …", Resume continues (on a server without partial-download support it starts over, like Chrome), Cancel → "Cancelled" and no partial file is left in the folder.
3. **Finished:** click the row in the flyout → the file opens; the folder button → Explorer opens with the file selected. Delete the file in Explorer, reopen the flyout → "Moved or deleted".
4. **Downloads page:** Ctrl+J (or ⋯ → Downloads, or "Show all downloads"): every download, newest first; search filters by name or address; Copy link, Remove (file stays), Clear finished, Open folder all work.
5. **Unknown size** (a server that doesn't send a size): the ring spins instead of filling; the text shows only the received amount.
6. **Ask where to save:** Settings → Downloads → turn on "Ask where to save" → a Save dialog appears for each download; Cancel in the dialog → no download. Also try changing the download folder in Settings.
7. **Restart mid-download:** start a big download, close AJBrowser, start again → it's listed as "Interrupted · AJBrowser was closed during the download" with a Retry button; Retry opens the link in a new tab and downloads it again (the blank tab stays for now; 4c closes it).
8. Check the flyout and page in the Light theme too, and with the window narrow.

**Phase 4c checks:**
Checks 9, 10 and 12 below (and Pause/Resume, check 2) were **run by the agent in the real app** via UI Automation on 2026-11-26 and passed (details in the plan, Checkpoint 4c). Still worth a quick look yourself with a real site and your mouse.

9. **Download link that opens a new tab** (`target=_blank`, many "Download" buttons do this): the new tab disappears by itself and the download keeps going to the end in the flyout. (This used to freeze.)
10. **Close the tab while downloading:** start a big download from a page, then close that tab (Ctrl+W) → the download continues; Pause/Resume still work. The log (⋯ → Open data folder → Logs) shows "Kept a closed tab's engine…" and later "Released a closed tab's engine…".
11. **Retry** (on an Interrupted download after a restart): the extra tab closes itself and the download restarts.
12. **Exit with a download running:** close the window → "1 download is not finished… Close anyway?" No keeps the browser open (downloads continue); Yes closes. Same when you close the last tab with Ctrl+W (No brings back a home tab).
13. **Settings → Downloads → Simultaneous video downloads:** the list shows 1–10 and remembers your choice (it matters from Phase 5, for videos).
14. **Old partial files:** create an empty file named `test.mp4.ajpart` in your Downloads folder, set its date to more than 7 days ago (or wait), start AJBrowser → it asks whether to delete it. (Optional; you'll rarely see this before Phase 5.)

## 6b. Still open from earlier phases: check Phase 3 (and anything left from 1, 2, 2.5)

**Phase 3 checks** (F5, Debug). Your data starts empty: the agent deleted its test database and settings.
1. **History:** visit 3–4 sites, then Ctrl+H (or ⋯ → History). They're listed under "Today" with times; the search box filters; the 🗑 removes one; clicking a row opens it in the tab, middle-click opens it in a background tab.
2. **Clear browsing data:** Ctrl+Shift+Delete → pick "Last hour" → Clear now → those entries are gone. Optionally tick "Also clear cookies…" (signs you out of sites); afterwards check the shield is still green and ads are still blocked.
3. **Star / Ctrl+D:** on a page, click ☆ → it turns blue, the page appears on the bookmarks bar, and a panel lets you rename it or pick a folder (saved when the panel closes). Click the blue star again → Remove.
4. **Bookmarks page:** Ctrl+Shift+O → New folder → select it, rename it in the right panel, Save. Move a bookmark into it (select → Folder → Save). Double-click the folder to open it, ↑ to go back. Delete works. The folder shows on the bar with a drop-down menu.
5. **Bookmarks bar:** Ctrl+Shift+B hides/shows it (also in Settings). Click a bookmark → opens here; middle-click → background tab.
6. **Suggestions:** in the address bar type the start of a visited site ("exa", "git"…) → a list appears (☆ = bookmark, 🕘 = history); ↑/↓ + Enter or click opens it; Esc closes the list, a second Esc restores the address.
7. **Settings** (⋯ → Settings, or type `ajbrowser://settings`): theme radio buttons switch at once; change the search engine to DuckDuckGo → searching from the address bar uses it; try Custom with `https://example.com/?q={query}`; change the home page (Enter to apply; a bad value shows a red message) → Home button and new tabs use it; the Block ads switch matches the shield.
8. **Session restore:** Settings → "Continue where you left off". Open 3–4 tabs (one of them History), close the window with ✕, start again → the same tabs come back, the active one loads, the others load when you click them.
9. **Restart keeps everything:** bookmarks, history and settings are still there after a restart.
10. Check the new pages in the Light theme too.
11. **Site icons:** visit a few sites, then open History, Bookmarks, the bookmarks bar and the address-bar suggestions → each shows the site's own icon, sharp (not blurry) at your 125% scaling. Sites visited before this update show a globe or a soft 16 px icon until you visit them again; sites with SVG icons (for example GitHub) keep a 16 px icon on purpose. With History and Bookmarks open in two tabs, switching between them shows the right page each time.
12. **No title bar** (tabs in the title bar, done early from Phase 9): drag the empty space right of the tabs to move the window; double-click it to maximize/restore; the − □ ✕ buttons at the top right work (✕ turns red); hover □ to get Windows 11's snap layouts; resize from all edges; when maximized nothing is cut off at the top; F11 fullscreen and back still work. If you use a second monitor or another scaling level, check there too.

**Bug fixed after Phase 3 (F17):** a popup tab that closes itself (a download link opened in a new tab, a sign-in popup) used to close the **whole browser**. Now only that tab closes. Please re-check: a site's "Sign in with Google" popup, and a download link that opens a new tab; also that Alt+F4 still closes the browser.

Known Phase 3 limits: bookmarks can't be reordered by dragging yet; closing the *last* tab closes the window and saves an empty session; history shows each page once, on the day of its latest visit; instantly-sent keystrokes right after Ctrl+T can lose their first characters (normal typing is fine).

**Phase 1 checks** (not reported yet): address bar (`example.com`, `best wpf tutorials`, `c#`), click-to-select-all and Esc, Back/Forward, Reload/Stop, `http://nonexistent.invalid` → error panel with Try again, Wi-Fi off → error panel, ending a `msedgewebview2.exe` renderer in Task Manager → crash panel, theme switch.

**Phase 2 checks** (F5, Debug):
1. **Tabs:** Ctrl+T opens a tab with the address bar focused. Open about 10 tabs: they shrink. Ctrl+Tab / Ctrl+Shift+Tab, Ctrl+1…8, Ctrl+9 switch. Ctrl+W, the tab's ✕ and middle-click close; the right neighbour becomes active. Ctrl+Shift+T brings the last closed tab back in its old place. Closing the last tab closes the window.
2. **Shortcuts while the page has focus (V7):** click inside a page first, then try Ctrl+T, Ctrl+W, Ctrl+L, Alt+D, F6, F5, Alt+Left/Right, Ctrl+Tab. Each should work exactly once (for example F5 reloads once; Ctrl+W doesn't close two tabs).
3. **Links and popups:** a `target=_blank` link opens a new tab next to the current one. Right-click a link → **Open link in new tab** opens it in the background. A site's "Sign in with Google/Microsoft" popup opens as a tab, works, and closes itself when done.
4. **Blocked popups:** on a site that opens popups by itself, a blue icon appears next to ⋯; its menu opens the blocked address in a new tab.
5. **Favicons and spinner:** tabs show site icons; a spinner while loading; a globe when the site has no icon.
6. **Find (V8):** Ctrl+F opens the find bar; type a word that appears 3 times → "1/3"; Enter / Shift+Enter move; Esc closes. **Tell the agent if the first match shows "0/3"** (the counter assumes WebView2 counts from 1).
7. **Zoom:** Ctrl+Plus / Ctrl+Minus / Ctrl+0 and Ctrl+mouse wheel; the "110%" button appears and resets on click.
8. **Fullscreen:** play a YouTube video fullscreen → tab strip and toolbar disappear, Esc restores. F11 toggles app fullscreen.
9. **Print:** Ctrl+P shows the print preview.
10. **Command line:** `AJBrowser.exe example.com C:\path\to\page.html` opens two tabs.

Known Phase 2 limits: middle-click / Ctrl+click on links open the tab in front (WebView2 doesn't say which button was used); beyond about 20 tabs (at 1280 px) the extra tabs are clipped (use Ctrl+Tab); tabs can't be dragged yet (Phase 9).

**Phase 2.5 checks** (F5, Debug):
1. **Installs itself:** the log (⋯ → Open data folder → `Logs`) has `Ad blocker ready: uBlock Origin Lite 2026.1124.1811, on`. To see a true first install, close the app and delete `%LOCALAPPDATA%\AJBrowser\Extensions\uBOLite\`, then start again: it reinstalls (log: `Installed uBlock Origin Lite …`).
2. **YouTube:** play 3–4 videos: no pre-roll ads.
3. **News site** (for example timesofindia.com or cnn.com): ad slots are visibly gone.
4. **Shield:** the shield left of ⋯ is green with a tick. Click it → panel with a **Block ads** switch. Switch off → the page reloads, ads come back, the shield turns grey with a slash; switch on → gone again. Esc or clicking elsewhere closes the panel.
5. **Restart keeps the choice:** turn it off, close, F5 → grey shield, ads visible. Turn it back on.
6. **Settings:** shield panel → **Settings** opens uBO Lite's dashboard in a new tab (filtering mode "Optimal").
7. Check the panel in the Light theme too (⋯ → Theme → Light).

Known Phase 2.5 limits: a future uBO Lite upgrade resets its own dashboard settings (new folder → new extension id); our on/off choice is kept. Other open tabs only change after they reload.

**Open issue (F16, deferred to Phase 8):** YouTube repeatedly shows "Experiencing interruptions?" while videos load. It's YouTube's own notice, most likely a reaction to uBO Lite (not yet confirmed with the on/off test). Expected fix: Phase 8 (own engine + scriptlets); meanwhile a newer uBO Lite release might help.

Report anything odd, and the agent will fix it before Phase 4.

**Updating uBO Lite later:** download the newer `uBOLite_<version>.chromium.zip` from github.com/uBlockOrigin/uBOL-home/releases, put it in `AJBrowser/Extensions/`, delete the old zip, and update the version and SHA-256 in `THIRD-PARTY-NOTICES.md`. The app upgrades itself on the next start.

**Suggested prompt (new session or new PC):**
> Read PROGRESS.md and IMPLEMENTATION_PLAN.md, then continue Phase 8 with 8d (or: fix what I found while trying 8c).

## 7. uBlock Origin Lite test (V5): done

Result in plan §11 F15. On the first PC the extension is registered in the **Diagnostics** profile from `D:\tools\uBOLite`; keep that folder, or click **Load unpacked…** again after moving it. The normal browser doesn't need that folder: since Phase 2.5 it installs its own copy from the bundled zip.

## 8. Diagnostics tool quick reference (Debug builds)

- Open it with the **Diagnostics** button, or run `AJBrowser.exe --diagnostics`.
- `--diagnostics-auto` runs the whole automated suite (about 5 minutes, needs internet), writes a report and exits. `--diagnostics-auto=media,popup,download,blocking` runs a subset.
- Reports are saved to `%LOCALAPPDATA%\AJBrowser\Diagnostics\Reports\spike-*.md`.
- It uses a separate WebView2 profile (`%LOCALAPPDATA%\AJBrowser\Diagnostics\WebView2`); delete that folder to reset it.
