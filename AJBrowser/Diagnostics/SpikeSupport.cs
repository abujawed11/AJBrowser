using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace AJBrowser.Diagnostics;

// Phase 0.5 spike helpers. Throwaway code: kept simple on purpose and excluded from Release builds.

internal static class Stats
{
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToArray();
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public static string F(double value, int decimals = 1) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
}

/// <summary>Collects report lines per section and writes them as Markdown.</summary>
internal sealed class SpikeReport
{
    private readonly List<(string Section, StringBuilder Body)> _sections = [];

    public void Add(string section, string line)
    {
        var entry = _sections.FirstOrDefault(s => s.Section == section);
        if (entry.Body is null)
        {
            entry = (section, new StringBuilder());
            _sections.Add(entry);
        }

        entry.Body.AppendLine(line);
    }

    public string Write(string directory, string header)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"spike-{DateTime.Now:yyyyMMdd-HHmmss}.md");

        var builder = new StringBuilder().AppendLine(header).AppendLine();
        foreach (var (section, body) in _sections)
        {
            builder.AppendLine($"## {section}").AppendLine().Append(body).AppendLine();
        }

        File.WriteAllText(path, builder.ToString());
        return path;
    }
}

/// <summary>
/// Measures how late a 16 ms UI-thread timer fires. Large lag means the UI thread was busy
/// (for example, handling WebResourceRequested events). Windows timer resolution adds ~15 ms of noise,
/// so compare modes against each other rather than reading absolute values.
/// </summary>
internal sealed class UiLagMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);
    private readonly DispatcherTimer _timer;
    private readonly List<double> _lagsMs = [];
    private long _last;

    public UiLagMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = Interval };
        _timer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
            _lagsMs.Add(Math.Max(0, elapsed - Interval.TotalMilliseconds));
            _last = now;
        };
    }

    public void Start()
    {
        _lagsMs.Clear();
        _last = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public double P95 => Stats.Percentile(_lagsMs, 95);
    public double Max => _lagsMs.Count == 0 ? 0 : _lagsMs.Max();
}
