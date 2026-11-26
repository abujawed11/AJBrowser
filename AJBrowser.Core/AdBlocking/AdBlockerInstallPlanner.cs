namespace AJBrowser.Core.AdBlocking;

/// <summary>What to do with the ad-block extension at startup.</summary>
public enum AdBlockerInstallAction
{
    /// <summary>Ours is installed, up to date and its folder is there: nothing to do.</summary>
    KeepInstalled,

    /// <summary>Nothing is installed: install the bundled version.</summary>
    Install,

    /// <summary>Something is installed but it's older, of unknown version, or its folder is gone: remove it, then install the bundled version.</summary>
    Replace,
}

/// <summary>
/// The startup decision as a pure function, so it can be unit-tested without WebView2.
/// </summary>
public static class AdBlockerInstallPlanner
{
    /// <param name="bundled">Version of the extension shipped with this build of AJBrowser.</param>
    /// <param name="isInstalledInProfile">The WebView2 profile lists our extension (matched by id or name).</param>
    /// <param name="installedVersion">The version we recorded when installing it, or null if unknown.</param>
    /// <param name="installedFolderExists">The folder WebView2 loads it from still has a manifest.json.</param>
    public static AdBlockerInstallAction Decide(Version bundled, bool isInstalledInProfile, Version? installedVersion, bool installedFolderExists)
    {
        ArgumentNullException.ThrowIfNull(bundled);

        if (!isInstalledInProfile)
        {
            return AdBlockerInstallAction.Install;
        }

        // Unknown version (state file lost, or someone loaded it by hand): we can't tell what's there, so replace it.
        // Missing folder: WebView2 can't load files that are gone, so the extension is broken.
        if (installedVersion is null || !installedFolderExists || installedVersion < bundled)
        {
            return AdBlockerInstallAction.Replace;
        }

        // Same version, or newer (an older AJBrowser running after a newer one): keep what works.
        return AdBlockerInstallAction.KeepInstalled;
    }
}
