using System.Globalization;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>One progress line of a yt-dlp download (see <see cref="YtDlpArguments.Download"/>'s template).</summary>
public sealed record YtDlpProgressLine(long Downloaded, long? Total, bool TotalIsEstimate, double? BytesPerSecond, double? EtaSeconds, string? FormatId);

/// <summary>What a yt-dlp run is doing.</summary>
public enum YtDlpStage
{
    Starting,
    Downloading,
    Merging,
    Done,
}

/// <summary>
/// Follows a yt-dlp download's output (plan Phase 7.5a): its progress lines (one per part: picture, then sound), the
/// merge step and the final path, and combines the parts into one figure for the downloads list.
/// Unknown lines are ignored; nothing in them is trusted beyond numbers and the final path.
/// </summary>
public sealed class YtDlpProgressTracker
{
    private readonly IReadOnlyList<string> _parts;
    private readonly long?[] _sizes;
    private readonly long[] _done;
    private int _current;

    /// <param name="formatSelector">"137+140" (two parts) or "18" (one).</param>
    /// <param name="expectedSizes">Known or estimated size per part, in the same order (null when unknown).</param>
    public YtDlpProgressTracker(string formatSelector, IReadOnlyList<long?>? expectedSizes = null)
    {
        _parts = formatSelector.Split('+');
        _sizes = new long?[_parts.Count];
        _done = new long[_parts.Count];
        for (var i = 0; i < _parts.Count && expectedSizes is not null && i < expectedSizes.Count; i++)
        {
            _sizes[i] = expectedSizes[i];
        }
    }

    public YtDlpStage Stage { get; private set; } = YtDlpStage.Starting;

    public double? BytesPerSecond { get; private set; }

    /// <summary>The finished file, from the <c>ajb-file:</c> line.</summary>
    public string? FinalPath { get; private set; }

    public long Received => _done.Sum();

    /// <summary>All parts' sizes added up, when each is known.</summary>
    public long? Total => _sizes.All(s => s is not null) ? _sizes.Sum(s => s!.Value) : null;

    /// <summary>Reads one output line; true when the figures changed.</summary>
    public bool Read(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        if (line.StartsWith(YtDlpArguments.FilePrefix, StringComparison.Ordinal))
        {
            var path = line[YtDlpArguments.FilePrefix.Length..].Trim();
            if (path.Length > 0 && Path.IsPathFullyQualified(path))
            {
                FinalPath = path;
                Stage = YtDlpStage.Done;
            }

            return true;
        }

        if (line.StartsWith("[Merger]", StringComparison.Ordinal) || line.StartsWith("[VideoRemuxer]", StringComparison.Ordinal))
        {
            Stage = YtDlpStage.Merging;
            BytesPerSecond = null;
            return true;
        }

        if (ParseLine(line) is not { } progress)
        {
            return false;
        }

        var index = progress.FormatId is { } id && IndexOf(id) is { } found ? found : _current;
        _current = index;
        _done[index] = progress.Downloaded;
        if (progress.Total is { } total)
        {
            _sizes[index] = total;
        }

        // Earlier parts are complete once a later one is downloading.
        for (var i = 0; i < index; i++)
        {
            if (_sizes[i] is { } size)
            {
                _done[i] = size;
            }
        }

        BytesPerSecond = progress.BytesPerSecond;
        Stage = YtDlpStage.Downloading;
        return true;
    }

    private int? IndexOf(string formatId)
    {
        for (var i = 0; i < _parts.Count; i++)
        {
            if (_parts[i] == formatId)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>"ajb-progress 1048576 5242880 NA 250000.5 16 137" → figures; "NA" (unknown) → null. Not a progress line → null.</summary>
    public static YtDlpProgressLine? ParseLine(string line)
    {
        if (!line.StartsWith(YtDlpArguments.ProgressPrefix + " ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = line[(YtDlpArguments.ProgressPrefix.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || Number(fields[0]) is not { } downloaded)
        {
            return null;
        }

        var exact = Number(fields[1]);
        var estimate = Number(fields[2]);
        var formatId = fields.Length > 5 && fields[5] != "NA" && fields[5].Length <= 100 ? fields[5] : null;
        return new YtDlpProgressLine(
            (long)downloaded,
            exact is { } e ? (long)e : estimate is { } s ? (long)s : null,
            exact is null && estimate is not null,
            Number(fields[3]),
            Number(fields[4]),
            formatId);

        static double? Number(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 && double.IsFinite(value) ? value : null;
    }
}
