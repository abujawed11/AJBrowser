using System.Diagnostics;

namespace AJBrowser.Services;

/// <summary>Opens files and folders with Windows Explorer / the default app.</summary>
public sealed class ShellService
{
    public void OpenFolder(string path)
    {
        // ArgumentList quotes the path for us; never build command lines by concatenation.
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>Opens a text file (for example the third-party notices) in Notepad, which every Windows PC has.</summary>
    public void OpenTextFile(string path)
    {
        var startInfo = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>Opens a web page in the user's default browser. Only http(s) URLs are allowed.</summary>
    public void OpenUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Only http(s) URLs can be opened.", nameof(url));
        }

        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
