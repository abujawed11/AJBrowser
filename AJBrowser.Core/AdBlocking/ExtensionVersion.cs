namespace AJBrowser.Core.AdBlocking;

/// <summary>
/// Chrome extension versions: one to four dot-separated whole numbers, each 0–65535, no leading zeros
/// (for example uBlock Origin Lite's "2026.1124.1811"). Missing parts count as 0, so "1.2" equals "1.2.0.0".
/// </summary>
public static class ExtensionVersion
{
    /// <summary>Parses a manifest "version" string. Returns null if it isn't a valid Chrome extension version.</summary>
    public static Version? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return null;
        }

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 5 || (part.Length > 1 && part[0] == '0') || !part.All(char.IsAsciiDigit))
            {
                return null;
            }

            numbers[i] = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (numbers[i] > 65535)
            {
                return null;
            }
        }

        // Always four parts, so comparisons treat "1.2" and "1.2.0.0" as equal.
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>The shortest text form: "2026.1124.1811" rather than "2026.1124.1811.0". Used for folder names and logs.</summary>
    public static string Format(Version version) =>
        version.Revision > 0 ? version.ToString(4)
        : version.Build > 0 ? version.ToString(3)
        : version.ToString(2);
}
