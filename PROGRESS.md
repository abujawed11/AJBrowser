# AJBrowser — Progress & Session Handoff

> **For Claude (or any coding agent) starting a new session:** read this file first, then
> `IMPLEMENTATION_PLAN.md` (the full roadmap). This file is the short "where are we" summary;
> the plan is the source of truth for scope, architecture, checklists and findings.

**Last updated:** 2026-11-26 · **Current phase:** Phase 0 ✅ · Phase 0.5 ✅ · Phases 1, 2 and 2.5 🔶 built, awaiting your manual checks · **Next: your checks, then Phase 3 (bookmarks, history, settings)**

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
   - Test Explorer: expect **205 passing tests**.
   - F5: the browser window opens with one tab and loads the Bing home page. Left of **⋯** is the ad-block shield (green = blocking; click for the on/off switch). The **⋯** menu has Theme, Open data folder, and the Debug-only **Diagnostics** / **Throw test exception** items.
   - On the very first run on a PC, the ad blocker installs itself (the first page waits about 4 s).
   - Make sure **AJBrowser** (not AJBrowser.Core.Tests) is the startup project. Running the test project just prints "exited with code 0".
4. **Per-PC data:** the app stores data in `%LOCALAPPDATA%\AJBrowser\` (logs, WebView2 profile, unpacked ad blocker, spike reports). That folder isn't in git, so a new PC starts empty, which is expected.

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
  App.xaml(.cs)              OnStartup composition, WebView2 runtime check, command-line URLs as tabs, global exception handlers, --diagnostics args (DEBUG)
  AppServices.cs             hand-written composition root (no DI container); CreateTab()
  Browser/BrowserEnvironment.cs  shared CoreWebView2Environment (lazy, recreatable), runtime check, profile "Default",
                             AreBrowserExtensionsEnabled = true
  Browser/ExtensionManager.cs    ad blocker: unpack bundled uBO Lite zip → install/upgrade in profile once per start,
                             on/off (state.json), settings page URL; every new WebView awaits PrepareAsync first
  Extensions/uBOLite_2026.1124.1811.chromium.zip  official release, unmodified (GPLv3), copied to output
  Browser/BrowserTab.cs      wraps one WebView2: Configure() = ALL per-WebView setup, navigation, crash recovery,
                             popups, favicon, zoom, find, fullscreen, context menu
  Browser/PopupRequest.cs    hands a new tab's WebView to WebView2 as a popup (deferral + NewWindow)
  Browser/PageProblem.cs     error/crash panel model + which navigation errors get a panel
  Services/ThemeService.cs   Fluent ThemeMode (WPF0001 suppressed here) + Light/Dark brush swap, follows Windows
  Services/ShellService.cs   open folder in Explorer, open URL in the default browser
  ViewModels/MainWindowViewModel.cs   tab list, selected tab, close/reopen, popups → tabs, fullscreen, theme, debug
  ViewModels/TabViewModel.cs address bar, title, favicon, loading, back/forward, zoom, find, blocked popups, problem panel
  Views/MainWindow.xaml(.cs) tab strip, toolbar, ⋯ menu, find bar, one WebView host per tab, problem panel;
                             code-behind: keyboard shortcuts, fullscreen, menus
  Views/Controls/            AddressBar, TabStrip (+ ShrinkingTabPanel), FindBar
  Themes/                    Light.xaml, Dark.xaml (semantic brushes), Shared.xaml (fonts, sizes, ToolbarButtonStyle)
  Diagnostics/               Phase 0.5 spike, DEBUG ONLY (excluded from Release via csproj)
AJBrowser.Core/              UI-free logic (net10.0), TreatWarningsAsErrors
  Infrastructure/AppPaths.cs %LOCALAPPDATA%\AJBrowser\{WebView2,Data,Filters,Extensions,Logs,Temp,Tools}
  AdBlocking/                ExtensionVersion, ExtensionPackage (read zip manifest, unpack, clean old),
                             AdBlockerInstallPlanner (keep/install/replace), AdBlockerState (state.json)
  Logging/                   LogEntry, ILogSink, AppLogger (always redacts), FileLogSink (daily, 5 MB roll, keep 10)
  Security/Redactor.cs       strips URL query/fragment/user-info; redacts Cookie/Authorization/Bearer in text
  Navigation/                UrlOrSearchResolver, UrlDisplay, BrowserDefaults (Bing home/search until Phase 3),
                             ZoomLevels, FindStatus, CommandLineUrls
  Tabs/                      ClosedTabStack, TabSelection
AJBrowser.Core.Tests/        xUnit: Redactor, AppPaths, logging, navigation, tabs, ad blocking (205 tests)
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
| 2 Tabs | 🔶 Built | Agent: build, 166 tests, launch with 2 startup tabs + script popup blocked. **You:** the manual checks in §6. |
| 2.5 Ad blocking (uBO Lite) | 🔶 Built | Agent: build, 205 tests, launches (installs on first start, reused on the next, off/on choice kept), toolbar shield + switch driven via UI Automation. **You:** ad blocking confirmed working; remaining checks in §6. |
| 3 Bookmarks, history, settings | ⏭ **Next** | After your checks |
| 4–10 | Not started | Phase 8 is the optional adblock-rust upgrade |

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

## 6. Next task: check Phases 1, 2 and 2.5, then Phase 3

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

Report anything odd, and the agent will fix it before Phase 3.

**Updating uBO Lite later:** download the newer `uBOLite_<version>.chromium.zip` from github.com/uBlockOrigin/uBOL-home/releases, put it in `AJBrowser/Extensions/`, delete the old zip, and update the version and SHA-256 in `THIRD-PARTY-NOTICES.md`. The app upgrades itself on the next start.

**Suggested prompt on the new PC:**
> Read PROGRESS.md and IMPLEMENTATION_PLAN.md, then implement Phase 3.

## 7. uBlock Origin Lite test (V5): done

Result in plan §11 F15. On the first PC the extension is registered in the **Diagnostics** profile from `D:\tools\uBOLite`; keep that folder, or click **Load unpacked…** again after moving it. The normal browser doesn't need that folder: since Phase 2.5 it installs its own copy from the bundled zip.

## 8. Diagnostics tool quick reference (Debug builds)

- Open it with the **Diagnostics** button, or run `AJBrowser.exe --diagnostics`.
- `--diagnostics-auto` runs the whole automated suite (about 5 minutes, needs internet), writes a report and exits. `--diagnostics-auto=media,popup,download,blocking` runs a subset.
- Reports are saved to `%LOCALAPPDATA%\AJBrowser\Diagnostics\Reports\spike-*.md`.
- It uses a separate WebView2 profile (`%LOCALAPPDATA%\AJBrowser\Diagnostics\WebView2`); delete that folder to reset it.
