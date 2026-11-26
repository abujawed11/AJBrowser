namespace AJBrowser.Core.Navigation;

/// <summary>Turns command-line arguments (for example from "Open with AJBrowser") into URLs to open as tabs.</summary>
public static class CommandLineUrls
{
    /// <summary>
    /// Options (anything starting with "-") are skipped. Full paths to existing files become file:// URLs.
    /// Everything else goes through the address-bar rules (URL or search); blocked input is dropped.
    /// </summary>
    public static IReadOnlyList<Uri> Parse(IEnumerable<string> args, UrlOrSearchResolver resolver, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(fileExists);

        var urls = new List<Uri>();
        foreach (var raw in args)
        {
            var arg = raw?.Trim() ?? string.Empty;
            if (arg.Length == 0 || arg.StartsWith('-'))
            {
                continue;
            }

            if (Path.IsPathFullyQualified(arg) && fileExists(arg))
            {
                urls.Add(new Uri(Path.GetFullPath(arg)));
                continue;
            }

            var resolution = resolver.Resolve(arg);
            if (resolution.Kind is AddressKind.Url or AddressKind.Search)
            {
                urls.Add(resolution.Uri!);
            }
        }

        return urls;
    }
}
