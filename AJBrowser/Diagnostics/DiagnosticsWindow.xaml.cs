using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Security;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AJBrowser.Diagnostics;

/// <summary>
/// Phase 0.5 feasibility spike (DEBUG builds only). Answers the plan's "Items to verify" by experiment
/// and writes a Markdown report. Code-behind heavy on purpose: this is a throwaway test bench, not app architecture.
/// It uses its own WebView2 user data folder so it never touches the real browsing profile.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private const string TestHost = "spike.ajbrowser.test";
    private const string ProfileName = "Diagnostics";

    internal const string Mp4Url = "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_5MB.mp4";
    internal const string HlsAppleUrl = "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8";
    internal const string HlsMuxUrl = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";
    internal const string DashUrl = "https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd";
    internal const string DefaultDownloadUrl = "https://proof.ovh.net/files/100Mb.dat";

    private readonly AppLogger _log;
    private readonly bool _autoRun;
    private readonly IReadOnlySet<string> _autoTests;
    private readonly string _root;
    private readonly string _userDataFolder;
    private readonly string _downloadFolder;
    private readonly string _reportFolder;
    private readonly string _testPagesFolder;
    private readonly ObservableCollection<string> _logLines = [];

    private readonly MediaProbe _media = new();
    private readonly BlockingProbe _blocking = new();
    private readonly UiLagMonitor _lag = new();
    private readonly SpikeReport _report = new();
    private readonly List<string> _webMessages = [];

    private CoreWebView2Environment? _environment;
    private WebView2? _webView;
    private CoreWebView2DownloadOperation? _download;
    private long _lastProgressLog;

    private TaskCompletionSource<NavigationResult>? _navigationWaiter;
    private ulong _currentNavigationId;
    private long _navigationStart;

    internal sealed record NavigationResult(bool Success, string Status, double Milliseconds);

    /// <param name="autoTests">
    /// null = interactive. Otherwise run these tests unattended, save the report and exit.
    /// An empty set means all tests: media, popup, download, blocking.
    /// </param>
    public DiagnosticsWindow(AppPaths paths, AppLogger log, IReadOnlySet<string>? autoTests)
    {
        InitializeComponent();
        _log = log;
        _autoRun = autoTests is not null;
        _autoTests = autoTests ?? new HashSet<string>();
        _root = Path.Combine(paths.Root, "Diagnostics");
        _userDataFolder = Path.Combine(_root, "WebView2");
        _downloadFolder = Path.Combine(_root, "Downloads");
        _reportFolder = Path.Combine(_root, "Reports");
        _testPagesFolder = Path.Combine(AppContext.BaseDirectory, "Diagnostics", "TestPages");
        Directory.CreateDirectory(_downloadFolder);

        LogList.ItemsSource = _logLines;
        DownloadUrlBox.Text = DefaultDownloadUrl;
        TrackingCombo.ItemsSource = Enum.GetValues<CoreWebView2TrackingPreventionLevel>();
        TrackingCombo.SelectedItem = CoreWebView2TrackingPreventionLevel.Balanced;
        _media.Log = Log;

        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => CloseWebView();
    }

    private CoreWebView2? Core => _webView?.CoreWebView2;

    // ---------------------------------------------------------------- setup

    private async Task InitializeAsync()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                // Needed for the extension experiment (V5). Environment options are fixed per user data folder.
                AreBrowserExtensionsEnabled = true,
                // Lets test videos start without a click so the automated suite can run unattended.
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            _environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options);
            await CreateWebViewAsync();

            var header = $"Runtime {_environment.BrowserVersionString} · SDK {SdkVersion} · {RuntimeInformation.OSDescription}";
            StatusText.Text = header + $" · Profile data: {_userDataFolder}";
            Log(header);

            if (_autoRun)
            {
                await RunAutomatedSuiteAsync(_autoTests);
                var path = SaveReport();
                _log.Info($"Automated spike suite finished. Report: {path}");
                Application.Current.Shutdown();
            }
            else
            {
                Core?.Navigate($"https://{TestHost}/popup-opener.html");
            }
        }
        catch (Exception ex)
        {
            Log($"INIT FAILED: {ex.GetType().Name}: {Redactor.Text(ex.Message)}");
            _log.Error("Diagnostics init failed", ex);
            if (_autoRun)
            {
                Application.Current.Shutdown(2);
            }
        }
    }

    private static string SdkVersion =>
        typeof(CoreWebView2).Assembly.GetName().Version?.ToString() ?? "unknown";

    private async Task CreateWebViewAsync()
    {
        var webView = new WebView2();
        WebViewHost.Child = webView;

        // V12: the WPF overload that takes controller options (profile name / InPrivate).
        var controllerOptions = _environment!.CreateCoreWebView2ControllerOptions();
        controllerOptions.ProfileName = ProfileName;
        controllerOptions.IsInPrivateModeEnabled = false;
        await webView.EnsureCoreWebView2Async(_environment, controllerOptions);
        _webView = webView;

        var core = webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(TestHost, _testPagesFolder, CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.Profile.PreferredTrackingPreventionLevel = SelectedTrackingLevel;

        _media.Attach(core);
        if (InterceptCheck.IsChecked == true)
        {
            _blocking.Attach(core, _environment);
        }

        Log("WebView created (profile 'Diagnostics').");
    }

    private void CloseWebView()
    {
        if (_webView is null)
        {
            return;
        }

        _media.Detach();
        _blocking.Detach();
        WebViewHost.Child = null;
        _webView.Dispose();
        _webView = null;
        Log("WebView closed and disposed.");
    }

    private CoreWebView2TrackingPreventionLevel SelectedTrackingLevel =>
        TrackingCombo.SelectedItem is CoreWebView2TrackingPreventionLevel level ? level : CoreWebView2TrackingPreventionLevel.Balanced;

    // ---------------------------------------------------------------- logging / report

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        _logLines.Add(line);
        if (_logLines.Count > 4000)
        {
            _logLines.RemoveAt(0);
        }

        LogList.ScrollIntoView(line);
        _log.Debug(message);
    }

    private string SaveReport()
    {
        var header =
            $"# AJBrowser Phase 0.5 spike report\n\n" +
            $"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}\n" +
            $"- WebView2 runtime: {_environment?.BrowserVersionString ?? "n/a"}\n" +
            $"- WebView2 SDK: {SdkVersion}\n" +
            $"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})\n" +
            $"- .NET: {Environment.Version}\n" +
            $"- URLs below are redacted (no query strings).";
        var path = _report.Write(_reportFolder, header);
        Log($"Report saved: {path}");
        return path;
    }

    // ---------------------------------------------------------------- navigation helpers

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.IsRedirected)
        {
            _currentNavigationId = e.NavigationId;
            _navigationStart = Stopwatch.GetTimestamp();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId != _currentNavigationId)
        {
            return;
        }

        var result = new NavigationResult(e.IsSuccess, e.IsSuccess ? "OK" : e.WebErrorStatus.ToString(),
            Stopwatch.GetElapsedTime(_navigationStart).TotalMilliseconds);
        Log($"Navigation completed: {result.Status} in {Stats.F(result.Milliseconds, 0)} ms");
        _navigationWaiter?.TrySetResult(result);
    }

    internal async Task<NavigationResult> NavigateAndWaitAsync(string url, TimeSpan timeout)
    {
        if (Core is null)
        {
            return new NavigationResult(false, "no webview", 0);
        }

        _navigationWaiter = new TaskCompletionSource<NavigationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Core.Navigate(url);
        var finished = await Task.WhenAny(_navigationWaiter.Task, Task.Delay(timeout));
        return finished == _navigationWaiter.Task
            ? await _navigationWaiter.Task
            : new NavigationResult(false, "timeout", timeout.TotalMilliseconds);
    }

    internal static string PlayerUrl(string type, string source) =>
        $"https://{TestHost}/player.html?type={type}&src={Uri.EscapeDataString(source)}";

    // ---------------------------------------------------------------- web messages (untrusted input)

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string text;
        try
        {
            text = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            Log("[msg] ignored non-string message");
            return;
        }

        // Any page can post messages; treat them as untrusted, size-limited hints.
        if (text.Length > 4096)
        {
            Log("[msg] ignored oversized message");
            return;
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            var kind = json.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : "?";
            var summary = $"{kind}: {json.RootElement.GetRawText()}";
            _webMessages.Add(summary);
            Log($"[msg] {summary}");
        }
        catch (JsonException)
        {
            Log("[msg] ignored malformed JSON");
        }
    }

    // ---------------------------------------------------------------- popups (V6)

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var line = $"NewWindowRequested: uri={Redactor.Url(e.Uri)}, IsUserInitiated={e.IsUserInitiated}, " +
                       $"features: {e.WindowFeatures.Width}x{e.WindowFeatures.Height} (hasSize={e.WindowFeatures.HasSize})";
            Log(line);
            _report.Add("Popups and window.opener (V6)", "- " + line);

            var popup = new Window { Title = "Spike popup", Width = 560, Height = 460, Owner = this };
            var popupView = new WebView2();
            popup.Content = popupView;
            popup.Show();
            Log("Popup step 1: window shown");

            var controllerOptions = _environment!.CreateCoreWebView2ControllerOptions();
            controllerOptions.ProfileName = ProfileName;
            await popupView.EnsureCoreWebView2Async(_environment, controllerOptions);
            Log("Popup step 2: EnsureCoreWebView2Async completed");

            var popupCore = popupView.CoreWebView2;
            // Per-WebView setup (virtual host mappings, scripts, filters) is NOT inherited by new windows.
            popupCore.SetVirtualHostNameToFolderMapping(TestHost, _testPagesFolder, CoreWebView2HostResourceAccessKind.Allow);
            popupCore.WebMessageReceived += OnWebMessageReceived;
            popupCore.NavigationCompleted += (_, args) =>
                Log($"Popup navigation completed: {(args.IsSuccess ? "OK" : args.WebErrorStatus.ToString())}, source={Redactor.Url(popupCore.Source)}");
            popupCore.WindowCloseRequested += (_, _) =>
            {
                Log("Popup called window.close() -> WindowCloseRequested");
                popup.Close();
            };
            popup.Closed += (_, _) => popupView.Dispose();

            // Assigning NewWindow (instead of navigating a fresh tab) is what keeps window.opener working.
            e.NewWindow = popupCore;
            e.Handled = true;
            Log("Popup step 3: NewWindow assigned");
        }
        catch (Exception ex)
        {
            Log($"NewWindowRequested failed: {ex.GetType().Name}: {Redactor.Text(ex.Message)}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------------------------------------------------------------- downloads (V3)

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        var fileName = Path.GetFileName(e.ResultFilePath);
        e.ResultFilePath = Path.Combine(_downloadFolder, fileName);
        e.Handled = true; // suppress the default download flyout
        _download = operation;

        Log($"DownloadStarting: {fileName}, total={operation.TotalBytesToReceive}, mime={operation.MimeType}, url={Redactor.Url(operation.Uri)}");

        operation.StateChanged += (_, _) => Log($"Download state={SafeState(operation)}");
        operation.BytesReceivedChanged += (_, _) =>
        {
            if (Stopwatch.GetElapsedTime(_lastProgressLog).TotalSeconds >= 2)
            {
                _lastProgressLog = Stopwatch.GetTimestamp();
                Log($"Download progress: {SafeState(operation)}");
            }
        };
    }

    internal static string SafeState(CoreWebView2DownloadOperation operation)
    {
        try
        {
            return $"{operation.State}, bytes={operation.BytesReceived:N0}/{operation.TotalBytesToReceive?.ToString("N0") ?? "?"}, " +
                   $"canResume={operation.CanResume}, interrupt={operation.InterruptReason}";
        }
        catch (Exception ex)
        {
            return $"<unavailable: {ex.GetType().Name}>";
        }
    }

    private string DescribeDownloadFolder()
    {
        try
        {
            var files = new DirectoryInfo(_downloadFolder).EnumerateFiles().Select(f => $"{f.Name}={f.Length:N0}").ToList();
            return files.Count == 0 ? "(empty)" : string.Join("; ", files);
        }
        catch (IOException ex)
        {
            return $"<{ex.GetType().Name}>";
        }
    }

    // ---------------------------------------------------------------- button handlers

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        var text = UrlBox.Text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        Core?.Navigate(text);
    }

    private void PlayMp4_Click(object sender, RoutedEventArgs e) => Core?.Navigate(PlayerUrl("mp4", Mp4Url));
    private void PlayHlsApple_Click(object sender, RoutedEventArgs e) => Core?.Navigate(PlayerUrl("hls", HlsAppleUrl));
    private void PlayHlsMux_Click(object sender, RoutedEventArgs e) => Core?.Navigate(PlayerUrl("hls", HlsMuxUrl));
    private void PlayDash_Click(object sender, RoutedEventArgs e) => Core?.Navigate(PlayerUrl("dash", DashUrl));
    private void PopupTest_Click(object sender, RoutedEventArgs e) => Core?.Navigate($"https://{TestHost}/popup-opener.html");

    private void MediaLog_Click(object sender, RoutedEventArgs e) => _media.LogMedia = MediaLogCheck.IsChecked == true;

    private void Intercept_Click(object sender, RoutedEventArgs e)
    {
        if (Core is null)
        {
            return;
        }

        if (InterceptCheck.IsChecked == true)
        {
            _blocking.Attach(Core, _environment!);
        }
        else
        {
            _blocking.Detach();
        }

        Log($"Interception {(_blocking.IsAttached ? "ON" : "OFF")}");
    }

    private void Block_Click(object sender, RoutedEventArgs e)
    {
        _blocking.BlockingEnabled = BlockCheck.IsChecked == true;
        Log($"Test-domain blocking {(_blocking.BlockingEnabled ? "ON" : "OFF")} (requires interception)");
    }

    private void Tracking_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Core is not null)
        {
            Core.Profile.PreferredTrackingPreventionLevel = SelectedTrackingLevel;
            Log($"Tracking prevention = {SelectedTrackingLevel}");
        }
    }

    private async void MeasureReload_Click(object sender, RoutedEventArgs e)
    {
        if (Core is null)
        {
            return;
        }

        var extensions = await DescribeExtensionsAsync();
        await MeasurePageAsync(Core.Source, $"manual (TP={SelectedTrackingLevel}, intercept={_blocking.IsAttached}, " +
                                            $"block={_blocking.BlockingEnabled}, extensions={extensions})");
        Log("Measurement recorded in the report (section 'Blocking benchmark').");
    }

    private async void Cookies_Click(object sender, RoutedEventArgs e)
    {
        if (Core is null || !Uri.TryCreate(Core.Source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log("Cookies: navigate to an https page first.");
            return;
        }

        // Only counts are recorded, never names or values.
        var cookies = await Core.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
        var line = $"- {Redactor.Url(uri)}: {cookies.Count} cookies, HttpOnly={cookies.Count(c => c.IsHttpOnly)}, " +
                   $"Secure={cookies.Count(c => c.IsSecure)}, session={cookies.Count(c => c.IsSession)}, " +
                   $"distinct domains={cookies.Select(c => c.Domain).Distinct().Count()}";
        Log("Cookies " + line);
        _report.Add("Cookies via CookieManager", line);
    }

    private async void LoadExtension_Click(object sender, RoutedEventArgs e)
    {
        if (Core is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the unpacked extension folder (contains manifest.json)" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var extension = await Core.Profile.AddBrowserExtensionAsync(dialog.FolderName);
            var line = $"- Loaded '{extension.Name}' (id {extension.Id}), enabled={extension.IsEnabled}";
            Log("Extension " + line);
            _report.Add("Browser extensions (V5)", line);
        }
        catch (Exception ex)
        {
            var line = $"- Load failed: {ex.GetType().Name}: {Redactor.Text(ex.Message)}";
            Log("Extension " + line);
            _report.Add("Browser extensions (V5)", line);
        }
    }

    private async void ListExtensions_Click(object sender, RoutedEventArgs e) =>
        Log("Extensions: " + await DescribeExtensionsAsync());

    private async void ToggleExtensions_Click(object sender, RoutedEventArgs e)
    {
        if (Core is null)
        {
            return;
        }

        foreach (var extension in await Core.Profile.GetBrowserExtensionsAsync())
        {
            await extension.EnableAsync(!extension.IsEnabled);
        }

        Log("Extensions toggled: " + await DescribeExtensionsAsync());
    }

    private async Task<string> DescribeExtensionsAsync()
    {
        if (Core is null)
        {
            return "n/a";
        }

        var list = await Core.Profile.GetBrowserExtensionsAsync();
        return list.Count == 0 ? "none" : string.Join("; ", list.Select(x => $"{x.Name} ({(x.IsEnabled ? "on" : "off")})"));
    }

    private void StartDownload_Click(object sender, RoutedEventArgs e) => Core?.Navigate(DownloadUrlBox.Text.Trim());

    private void PauseDownload_Click(object sender, RoutedEventArgs e) => TryDownloadAction(op => op.Pause(), "Pause");

    private void ResumeDownload_Click(object sender, RoutedEventArgs e) => TryDownloadAction(op => op.Resume(), "Resume");

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => TryDownloadAction(op => op.Cancel(), "Cancel");

    private void TryDownloadAction(Action<CoreWebView2DownloadOperation> action, string name)
    {
        if (_download is null)
        {
            Log($"{name}: no download yet");
            return;
        }

        try
        {
            action(_download);
            Log($"{name} called -> {SafeState(_download)}");
        }
        catch (Exception ex)
        {
            Log($"{name} failed: {ex.GetType().Name}: {Redactor.Text(ex.Message)}");
        }
    }

    private void CloseWebView_Click(object sender, RoutedEventArgs e) => CloseWebView();

    private async void RecreateWebView_Click(object sender, RoutedEventArgs e)
    {
        if (_webView is null && _environment is not null)
        {
            await CreateWebViewAsync();
        }
    }

    private async void RunSuite_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            await RunAutomatedSuiteAsync(new HashSet<string>());
            SaveReport();
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e) => SaveReport();

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_reportFolder);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add(_reportFolder);
        Process.Start(startInfo)?.Dispose();
    }
}
