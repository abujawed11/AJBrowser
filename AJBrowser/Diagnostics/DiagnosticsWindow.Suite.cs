using System.IO;
using AJBrowser.Core.Security;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Diagnostics;

/// <summary>The unattended experiments. Each one appends findings to the report.</summary>
public partial class DiagnosticsWindow
{
    private const string BenchmarkSection = "Blocking benchmark (V4, V11) and tracking prevention";

    private static readonly string[] BenchmarkSites =
    [
        "https://www.cnn.com/",
        "https://www.theguardian.com/international",
        "https://www.dailymail.co.uk/home/index.html",
        "https://timesofindia.indiatimes.com/",
    ];

    private bool _benchmarkHeaderWritten;

    /// <param name="tests">Subset of: media, popup, download, blocking. Empty = all.</param>
    internal async Task RunAutomatedSuiteAsync(IReadOnlySet<string> tests)
    {
        bool Selected(string name) => tests.Count == 0 || tests.Contains(name);

        Log($"=== Automated suite started ({(tests.Count == 0 ? "all" : string.Join(",", tests))}) ===");
        if (Selected("media"))
        {
            await RunMediaTestsAsync();
        }

        if (Selected("popup"))
        {
            await RunPopupTestAsync();
        }

        if (Selected("download"))
        {
            await RunDownloadTestAsync();
        }

        if (Selected("blocking"))
        {
            await RunBlockingBenchmarkAsync();
        }

        Log("=== Automated suite finished ===");
    }

    // ------------------------------------------------------------ V1 / V2 media

    private async Task RunMediaTestsAsync()
    {
        const string section = "Media responses (V1) and manifest bodies (V2)";
        (string Name, string Type, string Source)[] streams =
        [
            ("Direct MP4", "mp4", Mp4Url),
            ("HLS, Apple fMP4 multi-variant", "hls", HlsAppleUrl),
            ("HLS, Mux MPEG-TS", "hls", HlsMuxUrl),
            ("DASH, Akamai BBB", "dash", DashUrl),
        ];

        foreach (var (name, type, source) in streams)
        {
            Log($"--- Media test: {name}");
            _media.Reset();
            _webMessages.Clear();

            var navigation = await NavigateAndWaitAsync(PlayerUrl(type, source), TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(15)); // let the player fetch manifests and segments

            var byKind = _media.Observations.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count());
            var contentTypes = _media.Observations
                .Select(o => $"{o.Kind}:{o.ContentType ?? "(none)"}")
                .Distinct()
                .Take(10);

            _report.Add(section, $"### {name}");
            _report.Add(section, $"- Player page navigation: {navigation.Status}");
            _report.Add(section, $"- Responses seen: {_media.TotalResponses} total, {_media.PartialResponses} × 206, {_media.RedirectResponses} × 3xx");
            _report.Add(section, $"- Classified: " + string.Join(", ", Enum.GetValues<SpikeMediaKind>()
                .Where(k => k != SpikeMediaKind.Other)
                .Select(k => $"{k}={byKind.GetValueOrDefault(k)}")));
            _report.Add(section, $"- Content types: {string.Join("; ", contentTypes)}");
            _report.Add(section, $"- Request headers visible in WebResourceResponseReceived: Cookie in {_media.RequestsWithCookieHeaderVisible}, Authorization in {_media.RequestsWithAuthorizationHeaderVisible}");

            foreach (var sample in _media.Observations.Where(o => o.Kind == SpikeMediaKind.DirectMedia).Take(3))
            {
                _report.Add(section, $"- Direct media sample: {sample.Status} len={sample.ContentLength ?? "?"} range={sample.ContentRange ?? "-"} {sample.RedactedUrl}");
            }

            foreach (var body in _media.ManifestBodies.Take(4))
            {
                _report.Add(section, $"- Manifest body: {(body.Success ? "OK" : "FAILED")} ({body.Bytes} chars) {body.Detail} — {body.RedactedUrl}");
            }

            if (_media.ManifestBodies.Count > 4)
            {
                _report.Add(section, $"- … {_media.ManifestBodies.Count - 4} more manifest bodies; " +
                                     $"{_media.ManifestBodies.Count(b => b.Success)}/{_media.ManifestBodies.Count} OK overall");
            }

            foreach (var message in _webMessages.Where(m => !m.StartsWith("player", StringComparison.Ordinal) || m.Contains("after10s", StringComparison.Ordinal)))
            {
                _report.Add(section, $"- Page reported: `{message}`");
            }

            _report.Add(section, string.Empty);
        }
    }

    // ------------------------------------------------------------ V6 popups

    private async Task RunPopupTestAsync()
    {
        const string section = "Popups and window.opener (V6)";
        Log("--- Popup test");
        _webMessages.Clear();

        await NavigateAndWaitAsync($"https://{TestHost}/popup-opener.html?auto=1", TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromSeconds(6));

        var popupReport = _webMessages.FirstOrDefault(m => m.StartsWith("popup", StringComparison.Ordinal));
        var openerReport = _webMessages.FirstOrDefault(m => m.StartsWith("opener-received", StringComparison.Ordinal));
        _report.Add(section, $"- Popup reported: {popupReport ?? "(nothing — popup may not have opened)"}");
        _report.Add(section, $"- Opener received message from popup: {openerReport ?? "(no)"}");
        foreach (var note in _webMessages.Where(m => m.StartsWith("note", StringComparison.Ordinal)))
        {
            _report.Add(section, $"- Page note: `{note}`");
        }

        _report.Add(section, "- Note: this auto test opens the popup without a user gesture.");
    }

    // ------------------------------------------------------------ V3 downloads

    private async Task RunDownloadTestAsync()
    {
        const string section = "Browser-managed downloads (V3)";
        Log("--- Download test");
        ClearDownloadFolder();
        _download = null;

        Core?.Navigate(DownloadUrlBox.Text.Trim());
        for (var i = 0; i < 30 && _download is null; i++)
        {
            await Task.Delay(500);
        }

        if (_download is null)
        {
            _report.Add(section, "- DownloadStarting was NOT raised within 15 s (test URL unreachable?).");
            return;
        }

        var operation = _download;
        _report.Add(section, $"- DownloadStarting raised; Handled=true and custom ResultFilePath accepted.");
        await Task.Delay(TimeSpan.FromSeconds(4));
        _report.Add(section, $"- After 4 s: {SafeState(operation)}");

        // Pause / resume
        try
        {
            operation.Pause();
            await Task.Delay(1500);
            var pausedBytes = operation.BytesReceived;
            await Task.Delay(1500);
            _report.Add(section, $"- After Pause(): {SafeState(operation)}; bytes changed while paused: {operation.BytesReceived != pausedBytes}");
            operation.Resume();
            await Task.Delay(2500);
            _report.Add(section, $"- After Resume(): {SafeState(operation)}");
        }
        catch (Exception ex)
        {
            _report.Add(section, $"- Pause/Resume threw {ex.GetType().Name}: {Redactor.Text(ex.Message)}");
        }

        // Does the download survive closing the only WebView?
        _report.Add(section, $"- Download folder while in progress: {DescribeDownloadFolder()}");
        long BytesNow()
        {
            try
            {
                return (long)operation.BytesReceived;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        var before = BytesNow();
        CloseWebView();
        var samples = new List<long>();
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(1000);
            samples.Add(BytesNow());
        }

        var grew = samples.Count > 0 && samples[^1] > before + 256 * 1024;
        _report.Add(section, $"- Closed the only WebView mid-download. operation.BytesReceived before: {before:N0}; next 8 s: {string.Join(", ", samples.Select(s => s.ToString("N0")))}");
        _report.Add(section, $"- Download folder 8 s after close: {DescribeDownloadFolder()}");
        _report.Add(section, $"- **Download continued after WebView closed: {(grew ? "YES" : "NO")}** (operation state now: {SafeState(operation)})");
        _report.Add(section, "- Note: the CoreWebView2Environment object was still alive and no other WebView existed in it.");

        await CreateWebViewAsync();
        try
        {
            operation.Cancel();
        }
        catch (Exception)
        {
            // Operation may be gone with its WebView.
        }

        await Task.Delay(1500);
        ClearDownloadFolder();
    }

    private void ClearDownloadFolder()
    {
        foreach (var file in new DirectoryInfo(_downloadFolder).EnumerateFiles())
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                Log($"Could not delete {file.Name} (still in use)");
            }
        }
    }

    // ------------------------------------------------------------ V4 / V11 blocking + tracking prevention

    private async Task RunBlockingBenchmarkAsync()
    {
        Log("--- Blocking benchmark");
        (string Name, CoreWebView2TrackingPreventionLevel Tracking, bool Intercept, bool Block)[] modes =
        [
            ("Baseline: TP Balanced, no interception", CoreWebView2TrackingPreventionLevel.Balanced, false, false),
            ("TP Strict, no interception", CoreWebView2TrackingPreventionLevel.Strict, false, false),
            ("Interception (observe only)", CoreWebView2TrackingPreventionLevel.Balanced, true, false),
            ("Interception + test blocklist", CoreWebView2TrackingPreventionLevel.Balanced, true, true),
        ];

        _media.LogMedia = false;
        try
        {
            foreach (var site in BenchmarkSites)
            {
                // Warm-up load so DNS/cache effects hit every mode roughly equally.
                ApplyMode(CoreWebView2TrackingPreventionLevel.Balanced, intercept: false, block: false);
                await NavigateAndWaitAsync(site, TimeSpan.FromSeconds(30));
                await Task.Delay(3000);

                foreach (var mode in modes)
                {
                    ApplyMode(mode.Tracking, mode.Intercept, mode.Block);
                    await MeasurePageAsync(site, mode.Name);
                }
            }
        }
        finally
        {
            _media.LogMedia = MediaLogCheck.IsChecked == true;
            ApplyMode(SelectedTrackingLevel, InterceptCheck.IsChecked == true, BlockCheck.IsChecked == true);
        }

        _report.Add(BenchmarkSection, string.Empty);
        _report.Add(BenchmarkSection, "UI lag = how late a 16 ms UI-thread timer fired (includes ~15 ms Windows timer noise). Handler times exclude WebView2's own marshalling cost.");
    }

    private void ApplyMode(CoreWebView2TrackingPreventionLevel tracking, bool intercept, bool block)
    {
        if (Core is null)
        {
            return;
        }

        Core.Profile.PreferredTrackingPreventionLevel = tracking;
        if (intercept)
        {
            _blocking.Attach(Core, _environment!);
        }
        else
        {
            _blocking.Detach();
        }

        _blocking.BlockingEnabled = block;
    }

    private async Task MeasurePageAsync(string url, string label)
    {
        if (Core is null)
        {
            return;
        }

        if (!_benchmarkHeaderWritten)
        {
            _report.Add(BenchmarkSection, "| Site | Mode | Load | Responses | Intercepted | Blocked | Handler p50 / p99 / max (µs) | UI lag p95 / max (ms) | Source kinds |");
            _report.Add(BenchmarkSection, "|---|---|---|---|---|---|---|---|---|");
            _benchmarkHeaderWritten = true;
        }

        await NavigateAndWaitAsync("about:blank", TimeSpan.FromSeconds(5));
        _media.Reset();
        _blocking.Reset();
        _lag.Start();

        var navigation = await NavigateAndWaitAsync(url, TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(6)); // late ad/tracker requests
        _lag.Stop();

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var row = $"| {host} | {label} | {(navigation.Success ? Stats.F(navigation.Milliseconds, 0) + " ms" : navigation.Status)} | " +
                  $"{_media.TotalResponses} | {_blocking.Intercepted} | {_blocking.Blocked} | " +
                  $"{Stats.F(_blocking.HandlerP50)} / {Stats.F(_blocking.HandlerP99)} / {Stats.F(_blocking.HandlerMax)} | " +
                  $"{Stats.F(_lag.P95)} / {Stats.F(_lag.Max)} | {_blocking.DescribeSources()} |";
        _report.Add(BenchmarkSection, row);
        Log(row);

        if (_blocking.IsAttached && _blocking.ByContext.Count > 0)
        {
            Log($"Contexts: {_blocking.DescribeContexts()}");
        }
    }
}
