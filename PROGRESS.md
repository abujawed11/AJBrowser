# AJBrowser — Progress & Session Handoff

> **For Claude (or any coding agent) starting a new session:** read this file first, then
> `IMPLEMENTATION_PLAN.md` (the full roadmap). This file is the short "where are we" summary;
> the plan is the source of truth for scope, architecture, checklists and findings.

**Last updated:** 2026-11-26 · **Current phase:** Phase 0 ✅ · Phase 0.5 ✅ · Phase 1 🔶 built, awaiting your manual checks · **Next: Phase 2, then Phase 2.5 (ad blocking)**

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
   - Test Explorer: expect **113 passing tests**.
   - F5: the browser window opens and loads the Bing home page. The **⋯** menu (top right) has Theme, Open data folder, and the Debug-only **Diagnostics** / **Throw test exception** items.
   - Make sure **AJBrowser** (not AJBrowser.Core.Tests) is the startup project. Running the test project just prints "exited with code 0".
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
  App.xaml(.cs)              OnStartup composition, WebView2 runtime check, global exception handlers, --diagnostics args (DEBUG)
  AppServices.cs             hand-written composition root (no DI container); CreateTab()
  Browser/BrowserEnvironment.cs  shared CoreWebView2Environment (lazy, recreatable), runtime check, profile "Default"
  Browser/BrowserTab.cs      wraps one WebView2: Configure() = ALL per-WebView setup, navigation, crash recovery
  Browser/PageProblem.cs     error/crash panel model + which navigation errors get a panel
  Services/ThemeService.cs   Fluent ThemeMode (WPF0001 suppressed here) + Light/Dark brush swap, follows Windows
  Services/ShellService.cs   open folder in Explorer, open URL in the default browser
  ViewModels/MainWindowViewModel.cs   window state: active tab, theme, debug commands
  ViewModels/TabViewModel.cs address bar, title, loading, back/forward, problem panel
  Views/MainWindow.xaml(.cs) toolbar, ⋯ menu, loading bar, page area + problem panel
  Views/Controls/AddressBar  select-all on focus, Enter/Esc handling
  Themes/                    Light.xaml, Dark.xaml (semantic brushes), Shared.xaml (fonts, sizes, ToolbarButtonStyle)
  Diagnostics/               Phase 0.5 spike, DEBUG ONLY (excluded from Release via csproj)
AJBrowser.Core/              UI-free logic (net10.0), TreatWarningsAsErrors
  Infrastructure/AppPaths.cs %LOCALAPPDATA%\AJBrowser\{WebView2,Data,Filters,Logs,Temp,Tools}
  Logging/                   LogEntry, ILogSink, AppLogger (always redacts), FileLogSink (daily, 5 MB roll, keep 10)
  Security/Redactor.cs       strips URL query/fragment/user-info; redacts Cookie/Authorization/Bearer in text
  Navigation/                UrlOrSearchResolver, UrlDisplay, BrowserDefaults (Bing home/search until Phase 3)
AJBrowser.Core.Tests/        xUnit: Redactor, AppPaths, logging, navigation (113 tests)
```

**Packages:**
- App: `Microsoft.Web.WebView2` 1.0.4370.26, `CommunityToolkit.Mvvm` 8.4.2
- Tests: xUnit 2.9.3, `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1

## 5. Phase status

| Phase | Status | Notes |
|---|---|---|
| 0 Foundation | ✅ Done | User verified and committed ("phase 0 done") |
| 0.5 Feasibility spike | ✅ Done | Automated suite, cookie test and uBO Lite extension test (V5 → F15: blocks YouTube ads) done. Ad-block engine decided. |
| 1 Single-tab browser | 🔶 Built | Agent: build, 113 tests, launch + home page load. **You:** the manual checks in §6. |
| 2 Tabs | ⏭ **Next** (after your Phase 1 checks) | See plan "Phase 2" |
| 2.5 Ad blocking (uBO Lite) | Not started | New phase, decided 2026-11-26. See plan "Phase 2.5" |
| 3–10 | Not started | Phase 8 is now the optional adblock-rust upgrade |

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

**Decided:** ad blocking = uBlock Origin Lite via WebView2's extension API in Phase 2.5 (right after tabs); adblock-rust + scriptlets (Brave's approach) is an optional Phase 8 upgrade (plan §12).

**Open decisions:**
- **Theme:** the in-box Fluent `ThemeMode` is tentatively accepted. Revisit if visual problems appear.
- **Installer/updater:** Phase 10.

## 6. Next task: check Phase 1, then Phase 2 (tabs)

**Your manual checks for Phase 1** (F5 in Visual Studio, Debug):
1. Type `example.com`, then Enter: the page loads, the window title shows "Example Domain", the address bar shows `example.com`.
2. Type `best wpf tutorials`: Bing search results. Type `c#`: a search, not a site.
3. Click the address bar: the whole URL is selected and shows `https://…`. Type something, press Esc: it reverts.
4. Click a few links, then Back/Forward. Back is greyed out on the first page.
5. Reload. During a slow load the Reload icon turns into ✕ (Stop); press it.
6. Type `http://nonexistent.invalid`: the "Can't find this site" panel with **Try again**. Turn Wi-Fi off and reload a page: an error panel. Turn it back on, then Try again.
7. Crash test: in Task Manager, end a `msedgewebview2.exe` process with "renderer" in its command line (add the column "Command line"), and check you get the "This page crashed" panel. Ending the main `msedgewebview2.exe` process (the one with no `--type=`) should recover by itself and reload the page.
8. ⋯ menu, then Theme Dark/Light: the toolbar switches, and sites that support dark mode switch too.
9. Click a `target=_blank` link: it opens in the same page (Phase 1 behaviour until tabs exist).

Report anything odd, and the agent will fix it before Phase 2.

**Phase 2 in short** (full checklist: plan, Phase 2): a tab strip with favicons, new-window handling into real tabs (with our own popup blocker), reopen closed tab, keyboard shortcuts, find, zoom, fullscreen, the context menu "Open link in new tab".

**Suggested prompt on the new PC:**
> Read PROGRESS.md and IMPLEMENTATION_PLAN.md, then implement Phase 2.

## 7. uBlock Origin Lite test (V5): done

Result in plan §11 F15. On the first PC the extension is registered in the Diagnostics profile from `D:\tools\uBOLite`; keep that folder, or click **Load unpacked…** again after moving it. On a new PC, load it again (download the `.chromium.zip` from github.com/uBlockOrigin/uBOL-home/releases).

## 8. Diagnostics tool quick reference (Debug builds)

- Open it with the **Diagnostics** button, or run `AJBrowser.exe --diagnostics`.
- `--diagnostics-auto` runs the whole automated suite (about 5 minutes, needs internet), writes a report and exits. `--diagnostics-auto=media,popup,download,blocking` runs a subset.
- Reports are saved to `%LOCALAPPDATA%\AJBrowser\Diagnostics\Reports\spike-*.md`.
- It uses a separate WebView2 profile (`%LOCALAPPDATA%\AJBrowser\Diagnostics\WebView2`); delete that folder to reset it.
