using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AJBrowser.Core.Tools;

/// <summary>How an external program run ended. <see cref="ExitCode"/> −1 = it couldn't be started.</summary>
public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Output, IReadOnlyList<string> ErrorTail)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs an external tool (yt-dlp, Deno, FFmpeg) the safe way (plan §5.8): arguments through
/// <see cref="ProcessStartInfo.ArgumentList"/> (no command-line strings), no window, UTF-8 output read line by line,
/// cancellation kills the whole process tree. A missing or blocked program is a failed run, not an exception.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Lines of stderr kept for error messages.</summary>
    public const int ErrorTailLines = 30;

    /// <param name="onOutput">Each stdout line as it arrives (progress), on a background thread.</param>
    /// <param name="keepOutput">Also return stdout (for small outputs such as <c>--version</c>, or <c>-J</c> JSON); capped at <paramref name="maxOutputChars"/>.</param>
    public static async Task<ProcessResult> RunAsync(
        string exePath,
        IReadOnlyList<string> arguments,
        Action<string>? onOutput = null,
        bool keepOutput = false,
        CancellationToken cancellationToken = default,
        int maxOutputChars = 64 * 1024 * 1024)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // yt-dlp is a Python program: make it write UTF-8 regardless of the Windows code page.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = startInfo };
        var output = new List<string>();
        var outputChars = 0;
        var errors = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            onOutput?.Invoke(e.Data);
            if (keepOutput)
            {
                lock (output)
                {
                    outputChars += e.Data.Length;
                    if (outputChars <= maxOutputChars)
                    {
                        output.Add(e.Data);
                    }
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (errors)
            {
                errors.Enqueue(e.Data);
                while (errors.Count > ErrorTailLines)
                {
                    errors.Dequeue();
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(-1, [], [$"The program couldn't be started: {ex.Message}"]);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
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

        // WaitForExitAsync returns after the output streams are drained.
        lock (output)
        lock (errors)
        {
            return new ProcessResult(process.ExitCode, output.ToList(), errors.ToList());
        }
    }
}
