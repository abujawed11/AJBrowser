using System.IO;
using System.Runtime.InteropServices;

namespace AJBrowser.Services;

/// <summary>Windows "known folders" (they can be moved by the user, so never assume %USERPROFILE%\Downloads).</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads

    /// <summary>The user's Downloads folder, wherever they moved it (falls back to %USERPROFILE%\Downloads).</summary>
    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pointer) == 0)
                {
                    try
                    {
                        if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path)
                        {
                            return path;
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the usual location.
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
