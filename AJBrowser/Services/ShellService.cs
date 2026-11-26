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
}
