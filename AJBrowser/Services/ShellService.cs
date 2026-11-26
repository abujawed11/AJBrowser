using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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

    /// <summary>Opens a file with its default app, like double-clicking it in Explorer (Windows' own safety checks apply).</summary>
    public void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Opens the file's folder in Explorer with the file selected. Uses the shell API rather than
    /// "explorer /select,…", whose command-line parsing trips over commas and quotes in paths.
    /// If the file is gone, just opens its folder (if that still exists).
    /// </summary>
    public void ShowInFolder(string path)
    {
        if (File.Exists(path))
        {
            var item = ILCreateFromPathW(path);
            if (item != IntPtr.Zero)
            {
                try
                {
                    if (SHOpenFolderAndSelectItems(item, 0, null, 0) == 0)
                    {
                        return;
                    }
                }
                finally
                {
                    ILFree(item);
                }
            }
        }

        var folder = Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder))
        {
            OpenFolder(folder);
        }
        else
        {
            throw new DirectoryNotFoundException("The folder no longer exists.");
        }
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr item);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[]? items, uint flags);
}
