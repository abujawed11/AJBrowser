using System.Diagnostics;
using System.Globalization;

namespace AJBrowser.Core.Media.Ffmpeg;

/// <summary>
/// Finds FFmpeg (plan Phase 6, option C): the path set in Settings, else <c>ffmpeg.exe</c> on the PATH. AJBrowser
/// doesn't ship or download it (yet); without it HLS still works (fMP4 → .mp4, TS → .ts).
/// </summary>
public static class FfmpegLocator
{
    public const string ExeName = "ffmpeg.exe";

    /// <param name="configuredPath">Settings → FFmpeg (a file, or a folder that holds ffmpeg.exe). Null/empty: PATH only.</param>
    /// <param name="pathVariable">The PATH to search (tests pass their own).</param>
    public static string? Find(string? configuredPath, string? pathVariable = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var candidate = configuredPath.Trim().Trim('"');
            if (Directory.Exists(candidate))
            {
                candidate = Path.Combine(candidate, ExeName);
            }

            if (File.Exists(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(candidate);
            }
        }

        foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), ExeName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry: skip it.
            }
        }

        return null;
    }
}

/// <summary>How an FFmpeg run ended. <see cref="ErrorTail"/> is the last lines it wrote to stderr (for the log).</summary>
public sealed record FfmpegResult(int ExitCode, string ErrorTail)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs FFmpeg as a separate process (plan §5.8, §6.4): arguments through <see cref="ProcessStartInfo.ArgumentList"/>
/// (no command-line string building), only local files AJBrowser created, never URLs or cookies. Progress comes from
/// <c>-progress pipe:1</c>; cancelling kills the whole process tree.
/// </summary>
public static class FfmpegRunner
{
    /// <summary>
    /// Arguments to copy (not re-encode) a local file into another container: video and audio streams only (TS files
    /// often carry timed-metadata streams MP4 can't hold), timestamps regenerated (ad breaks/discontinuities), and for
    /// MP4 the index at the front so players can start before the end is read. Recent FFmpeg inserts the ADTS→MP4 AAC
    /// conversion by itself; passing it explicitly would break MP3 audio, so we don't.
    /// </summary>
    public static IReadOnlyList<string> RemuxArguments(string input, string output, string container)
    {
        var format = container.ToUpperInvariant() switch
        {
            "MP4" => "mp4",
            "WEBM" => "webm",
            _ => "matroska",
        };

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-fflags", "+genpts",
            "-i", input,
            "-map", "0:v?", "-map", "0:a?",
            "-c", "copy",
        };
        if (format == "mp4")
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.AddRange(["-f", format, "-progress", "pipe:1", "-nostats", output]);
        return args;
    }

    public static async Task<FfmpegResult> RunAsync(string ffmpegPath, IReadOnlyList<string> arguments, double? durationSeconds, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var errorTail = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (errorTail)
            {
                errorTail.Enqueue(e.Data);
                while (errorTail.Count > 20)
                {
                    errorTail.Dequeue();
                }
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (FfmpegProgress.TryParse(e.Data, durationSeconds) is { } fraction)
            {
                progress?.Report(fraction);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw;
        }

        string tail;
        lock (errorTail)
        {
            tail = string.Join(Environment.NewLine, errorTail);
        }

        return new FfmpegResult(process.ExitCode, tail);
    }
}

/// <summary>Reads FFmpeg's <c>-progress</c> output ("out_time_us=12345678", "progress=end").</summary>
public static class FfmpegProgress
{
    /// <summary>The fraction done (0–1) from one progress line, or null if the line doesn't tell.</summary>
    public static double? TryParse(string? line, double? durationSeconds)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        if (line.Equals("progress=end", StringComparison.Ordinal))
        {
            return 1;
        }

        if (durationSeconds is not > 0)
        {
            return null;
        }

        double? seconds = null;
        // out_time_ms is in microseconds too (a long-standing FFmpeg naming quirk).
        if ((line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            && long.TryParse(line[(line.IndexOf('=') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) && micros >= 0)
        {
            seconds = micros / 1_000_000.0;
        }
        else if (line.StartsWith("out_time=", StringComparison.Ordinal)
                 && TimeSpan.TryParse(line["out_time=".Length..], CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero)
        {
            seconds = time.TotalSeconds;
        }

        return seconds is { } s ? Math.Clamp(s / durationSeconds.Value, 0, 1) : null;
    }
}
