# AJBrowser — Progress & Session Handoff

> **For Claude (or any coding agent) starting a new session:** read this file first, then
> `IMPLEMENTATION_PLAN.md` (the full roadmap). This file is the short "where are we" summary;
> the plan is the source of truth for scope, architecture, checklists and findings.

**Last updated:** 2026-11-26 · **Current phase:** Phase 0 ✅ · Phase 0.5 ✅ (optional item pending) · **Next: Phase 1**

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
   - Test Explorer: expect **41 passing tests**.
   - F5: a window titled **AJBrowser** opens, with a Theme selector and Debug-only **Diagnostics** / **Throw test exception** buttons.
4. **Per-PC data:** the app stores data in `%LOCALAPPDATA%\AJBrowser\` (logs, WebView2 profile, spike reports). That folder isn't in git, so a new PC starts empty, which is expected.

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
IMPLEMENTATION_PLAN.md       full roadmap (phases 0–10), findings, decisions
PROGRESS.md                  this file
AJBrowser/                   WPF app (net10.0-windows)
  App.xaml(.cs)              OnStartup composition, global exception handlers, --diagnostics args (DEBUG)
  AppServices.cs             hand-written composition root (no DI container)
  Services/ThemeService.cs   Fluent ThemeMode (WPF0001 suppressed here) + Light/Dark brush swap, follows Windows
  Services/ShellService.cs   open folder in Explorer
  ViewModels/MainWindowViewModel.cs   CommunityToolkit.Mvvm ([ObservableProperty], [RelayCommand])
  Views/MainWindow.xaml(.cs) placeholder chrome bar + content area (no browser yet)
  Themes/                    Light.xaml, Dark.xaml (semantic brushes), Shared.xaml (fonts, sizes)
  Diagnostics/               Phase 0.5 spike, DEBUG ONLY (excluded from Release via csproj)
AJBrowser.Core/              UI-free logic (net10.0), TreatWarningsAsErrors
  Infrastructure/AppPaths.cs %LOCALAPPDATA%\AJBrowser\{WebView2,Data,Filters,Logs,Temp,Tools}
  Logging/                   LogEntry, ILogSink, AppLogger (always redacts), FileLogSink (daily, 5 MB roll, keep 10)
  Security/Redactor.cs       strips URL query/fragment/user-info; redacts Cookie/Authorization/Bearer in text
AJBrowser.Core.Tests/        xUnit: Redactor, AppPaths, logging (41 tests)
```

**Packages:**
- App: `Microsoft.Web.WebView2` 1.0.4370.26, `CommunityToolkit.Mvvm` 8.4.2
- Tests: xUnit 2.9.3, `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1

## 5. Phase status

| Phase | Status | Notes |
|---|---|---|
| 0 Foundation | ✅ Done | User verified and committed ("phase 0 done") |
| 0.5 Feasibility spike | ✅ Done | Automated suite + manual cookie test done. **Optional/pending:** uBlock Origin Lite extension test (V5). |
| 1 Single-tab browser | ⏭ **Next** | See §6 below |
| 2–10 | Not started | See plan |

**Key spike findings to remember** (full table in plan §11, F1–F14):
- Media responses expose content type, length and range, and manifest bodies are readable. But content types are unreliable, DASH segments look like `video/mp4`, and fMP4 HLS uses a byte-range `main.mp4`, so suppress anything a manifest references.
- Request interception is fast enough (about 25–70 µs per request), and service-worker requests are covered.
- Tracking prevention Strict greatly reduces tracker requests.
- **Closing a WebView freezes its downloads.** Keep the owning WebView alive until they finish (Phase 4).
- A navigation that becomes a download completes with `ConnectionAborted`. Don't show an error page for it (Phase 1).
- New-window WebViews **don't inherit** per-WebView setup, so use one shared `BrowserTab` initialisation. WebView2 does **not** block script popups; `IsUserInitiated` distinguishes clicked popups from script popups (Phase 2).
- `CookieManager` returns HttpOnly cookies (GitHub: 12 of 18).
- CDNs put tokens in URL **paths**, so a `Redactor.MediaUrl` is needed (Phase 5).

**Open decisions:**
- **Ad-block engine** (plan §6.5 and §12). Options: uBO Lite extension, adblock-rust via a Rust shim (Rust isn't installed), or a C# engine. Decide before Phase 8, ideally after the extension test.
- **Theme:** the in-box Fluent `ThemeMode` is tentatively accepted. Revisit if visual problems appear.
- **Installer/updater:** Phase 10.

## 6. Next task: Phase 1 (working single-tab browser)

Full checklist: `IMPLEMENTATION_PLAN.md` → "Phase 1". In short:
- **`BrowserEnvironment`:** creates one shared WebView2 environment, with the user data folder from `AppPaths.WebView2UserData`.
- **Runtime check:** if the WebView2 runtime is missing, show a friendly dialog with the download link.
- **`BrowserTab`:** wraps the WebView2 control. Put all per-WebView setup in one init method.
- **`UrlOrSearchResolver`** (Core) with about 30 unit tests: URL vs. search text, bare domains, localhost, IPs, `javascript:` blocked.
- **Toolbar:** Back, Forward, Reload/Stop, Home, and an address bar (select-all on focus, Enter navigates, Esc reverts), plus a loading indicator.
- **Error states:** a friendly page with Retry, but none for a download's `ConnectionAborted`.
- **Crash handling:** recover from `ProcessFailed` (renderer crash, browser process exit).
- **Defaults:** tracking prevention Balanced; `PreferredColorScheme` follows the app theme.
- **Leave the Diagnostics window in place** (it's DEBUG-only and useful for later phases).

**Suggested prompt on the new PC:**
> Read PROGRESS.md and IMPLEMENTATION_PLAN.md, then implement Phase 1.

## 7. Optional pending manual test: uBlock Origin Lite (V5)

1. Download uBlock Origin Lite for **Chromium** (from its GitHub releases) and unzip it to a folder.
2. F5, then open **Diagnostics**, click **Load unpacked…**, and pick the folder that contains `manifest.json`.
3. Open an ad-heavy news site in the Diagnostics address bar. Click **Measure reload**, then **Toggle on/off**, then **Measure reload** again.
4. Click **Save report** and give the report to Claude, which records it as finding F15 and helps decide the ad-block engine.

## 8. Diagnostics tool quick reference (Debug builds)

- Open it with the **Diagnostics** button, or run `AJBrowser.exe --diagnostics`.
- `--diagnostics-auto` runs the whole automated suite (about 5 minutes, needs internet), writes a report and exits. `--diagnostics-auto=media,popup,download,blocking` runs a subset.
- Reports are saved to `%LOCALAPPDATA%\AJBrowser\Diagnostics\Reports\spike-*.md`.
- It uses a separate WebView2 profile (`%LOCALAPPDATA%\AJBrowser\Diagnostics\WebView2`); delete that folder to reset it.
