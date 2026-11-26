# AJBrowser

A Windows desktop browser built with C#, WPF and Microsoft WebView2, with a planned download manager,
video quality picker and built-in ad/tracker blocking.

The roadmap and progress tracker is **[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)**.

## Requirements

- Windows 10 or 11 (x64)
- Visual Studio 2026 (or the .NET CLI) with a .NET SDK that can build `net10.0` (.NET 10 or newer)
- .NET 10 Desktop Runtime (to run)
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11; used from Phase 1)

## Solution layout

| Project | Purpose |
|---|---|
| `AJBrowser` | WPF app (`net10.0-windows`): views, view models, WebView2 glue, Windows services |
| `AJBrowser.Core` | UI-free logic (`net10.0`): paths, logging, redaction, later parsers, downloads, persistence |
| `AJBrowser.Core.Tests` | xUnit tests for `AJBrowser.Core` |

## Build, run, test

In Visual Studio: open `AJBrowser.slnx`, make sure **AJBrowser** is the startup project, then press **F5**.
Run the tests from **Test Explorer**.

From a terminal in the repository root:

```powershell
dotnet build AJBrowser.slnx
dotnet run --project AJBrowser
dotnet test AJBrowser.Core.Tests
```

## Where the app stores data

Everything is per-user under `%LOCALAPPDATA%\AJBrowser\`:

| Folder | Contents |
|---|---|
| `WebView2\` | Browser profile: cookies, cache, site storage |
| `Data\` | Database and settings |
| `Filters\` | Ad-block filter lists |
| `Extensions\` | The unpacked ad blocker (uBlock Origin Lite) the browser loads, plus `state.json` (on/off) |
| `Logs\` | Application logs, one file per day. URLs have query strings removed, and cookie/authorization values are redacted. |
| `Temp\` | Scratch files for downloads |
| `Tools\` | Optional external tools (for example FFmpeg) |
