using System.Globalization;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Tools;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels.Pages;

/// <summary>
/// One tool row in Settings → Downloads (Phase 7.5b): "yt-dlp · Installed by AJBrowser: version 2026.11.23" with
/// Install/Update, Choose… (your own copy) and Clear. Installing shows its progress in the row.
/// </summary>
public sealed partial class ToolRowViewModel : ObservableObject
{
    private readonly ToolService _tools;

    public ToolRowViewModel(ToolKind kind, ToolService tools)
    {
        Kind = kind;
        _tools = tools;
        Refresh();
    }

    public ToolKind Kind { get; }

    public string Name => Kind switch
    {
        ToolKind.YtDlp => "yt-dlp",
        ToolKind.Deno => "Deno (for YouTube)",
        _ => "FFmpeg",
    };

    public string Licence => ToolCatalog.For(Kind).Licence;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>"Downloading 12.3 MB of 23.6 MB…", "Testing…", or the outcome of the last click.</summary>
    [ObservableProperty]
    public partial string Progress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>"Install" when AJBrowser has no copy, else "Update".</summary>
    public string InstallLabel => _tools.Installed(Kind) is null ? "Install" : "Update";

    public bool HasCustomPath => _tools.ConfiguredPath(Kind) is not null;

    public void Refresh()
    {
        Status = _tools.Status(Kind);
        IsBusy = _tools.IsInstalling(Kind);
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(HasCustomPath));
    }

    public void ChoosePath(string path) => _tools.SetPath(Kind, path);

    [RelayCommand]
    private void ClearPath() => _tools.SetPath(Kind, null);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        IsBusy = true;
        HasError = false;
        Progress = "Asking GitHub for the latest version…";
        var before = _tools.Installed(Kind)?.Version;
        var result = await _tools.InstallAsync(Kind, new Progress<ToolInstallProgress>(p => Progress = p.Stage switch
        {
            "Downloading" => p.TotalBytes is { } total
                ? $"Downloading {DownloadText.Size(p.ReceivedBytes, CultureInfo.CurrentCulture)} of {DownloadText.Size(total, CultureInfo.CurrentCulture)}…"
                : $"Downloading {DownloadText.Size(p.ReceivedBytes, CultureInfo.CurrentCulture)}…",
            "Checking" => "Checking the download's checksum…",
            "Unpacking" => "Unpacking…",
            "Testing" => "Testing that it starts…",
            _ => p.Stage,
        }));

        IsBusy = false;
        if (result.Tool is { } tool)
        {
            Progress = before == tool.Version ? $"Up to date ({tool.Version})." : $"Installed version {tool.Version}.";
        }
        else
        {
            HasError = true;
            Progress = result.Error ?? "Installing failed.";
        }

        Refresh();
    }

    private bool CanInstall() => !IsBusy;
}
