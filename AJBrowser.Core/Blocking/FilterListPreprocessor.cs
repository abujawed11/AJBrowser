using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AJBrowser.Core.Blocking;

/// <summary>
/// Handles the uBO list directives adblock-rust doesn't: <c>!#if</c> / <c>!#else</c> / <c>!#endif</c> blocks (rules
/// that only apply to some browsers), <c>!#include</c> (a list made of several files), and reads the list's header
/// (<c>! Title:</c>, <c>! Expires:</c>).
/// <para>Conditions are evaluated as uBO does for a Chromium/Edge browser that isn't uBO Lite (MV3): see
/// <see cref="BrowserTokens"/>. Unknown tokens count as false, as in uBO.</para>
/// </summary>
public static partial class FilterListPreprocessor
{
    /// <summary>The tokens that are true for AJBrowser (Chromium engine, Edge flavour, full scriptlet support).</summary>
    public static IReadOnlySet<string> BrowserTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "env_chromium",
        "env_edge",
        "ext_ublock",
        "cap_user_stylesheet",
    };

    public const int MaxNesting = 32;

    /// <summary>The list text with inactive blocks and directive lines removed, plus what the header says.</summary>
    /// <param name="Text">Lines adblock-rust should load.</param>
    /// <param name="Includes">Raw <c>!#include</c> targets from active parts, in order (not yet validated).</param>
    /// <param name="Expires">The <c>! Expires:</c> value, if present and readable.</param>
    /// <param name="Title">The <c>! Title:</c> value, if present.</param>
    public sealed record Result(string Text, IReadOnlyList<string> Includes, TimeSpan? Expires, string? Title);

    public static Result Process(string text, IReadOnlySet<string>? trueTokens = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        trueTokens ??= BrowserTokens;

        var output = new StringBuilder(text.Length);
        var includes = new List<string>();
        // Each frame: was the enclosing block active, and is this branch active.
        var frames = new Stack<(bool ParentActive, bool Condition, bool InElse)>();
        var active = true;
        TimeSpan? expires = null;
        string? title = null;
        var inHeader = true;

        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("!#", StringComparison.Ordinal))
            {
                inHeader = false;
                if (line.StartsWith("!#if ", StringComparison.Ordinal))
                {
                    var condition = Evaluate(line[5..], trueTokens);
                    if (frames.Count >= MaxNesting)
                    {
                        // Absurd nesting: treat the rest as inactive rather than recurse without limit.
                        condition = false;
                    }

                    frames.Push((active, condition, false));
                    active = active && condition;
                }
                else if (line == "!#else")
                {
                    // A stray !#else (no !#if) or a second one in the same block is ignored.
                    if (frames.Count > 0 && !frames.Peek().InElse)
                    {
                        var frame = frames.Pop();
                        frames.Push((frame.ParentActive, frame.Condition, true));
                        active = frame.ParentActive && !frame.Condition;
                    }
                }
                else if (line == "!#endif")
                {
                    if (frames.TryPop(out var frame))
                    {
                        active = frame.ParentActive;
                    }
                }
                else if (line.StartsWith("!#include ", StringComparison.Ordinal))
                {
                    if (active)
                    {
                        includes.Add(line[10..].Trim());
                    }
                }

                // Other "!#" lines (uBO's !#trusted…) are directives, never rules.
                continue;
            }

            if (inHeader)
            {
                if (line.StartsWith('!'))
                {
                    expires ??= ParseExpires(line);
                    title ??= ParseHeaderValue(line, "Title");
                }
                else if (line.Length > 0 && !line.StartsWith('['))
                {
                    inHeader = false;
                }
            }

            if (active)
            {
                output.Append(line).Append('\n');
            }
        }

        return new Result(output.ToString(), includes, expires, title);
    }

    /// <summary>Evaluates <c>a</c>, <c>!a</c>, <c>a &amp;&amp; b</c>, <c>a || !b</c> (|| binds looser, as in JS).</summary>
    internal static bool Evaluate(string expression, IReadOnlySet<string> trueTokens)
    {
        var any = false;
        foreach (var alternative in expression.Split("||"))
        {
            var all = true;
            var sawAtom = false;
            foreach (var part in alternative.Split("&&"))
            {
                var atom = part.Trim().Trim('(', ')').Trim();
                var negate = false;
                while (atom.StartsWith('!'))
                {
                    negate = !negate;
                    atom = atom[1..].TrimStart();
                }

                if (atom.Length == 0)
                {
                    all = false;
                    continue;
                }

                sawAtom = true;
                all &= trueTokens.Contains(atom) != negate;
            }

            any |= all && sawAtom;
        }

        return any;
    }

    /// <summary>Reads "! Expires: 4 days", "! Expires: 8 hours", "! Expires: 1 day (update frequency)".</summary>
    internal static TimeSpan? ParseExpires(string line)
    {
        var match = ExpiresRegex().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return null;
        }

        var unit = match.Groups[2].Value.ToLowerInvariant();
        return unit switch
        {
            "" or "d" or "day" or "days" => TimeSpan.FromDays(Math.Min(amount, 365)),
            "h" or "hour" or "hours" => TimeSpan.FromHours(Math.Min(amount, 24 * 365)),
            _ => null, // minutes, weeks, typos: use the default rather than guess
        };
    }

    private static string? ParseHeaderValue(string line, string key)
    {
        var body = line.TrimStart('!').TrimStart();
        if (!body.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = body[(key.Length + 1)..].Trim();
        return value.Length is > 0 and <= 200 ? value : null;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    [GeneratedRegex(@"^!\s*Expires\s*:\s*(\d{1,4})\s*([A-Za-z]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ExpiresRegex();
}
