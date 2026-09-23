using System.Runtime.InteropServices;
using FarmingRpgMaker.App.Mvvm;
using FarmingRpgMaker.App.Services;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.ViewModels;

public sealed class AboutViewModel(string version, IUrlLauncher launcher) : ObservableObject
{
    public string Title => "Farming RPG Maker";

    public string VersionText => $"Version {version}";

    public string Description =>
        "Build and play cozy farming RPGs: design maps, crops, NPCs and quests, then jump straight into play mode.";

    public string RuntimeText =>
        $"{RuntimeInformation.FrameworkDescription} · Avalonia {typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3)} · {RuntimeInformation.OSDescription}";

    public RelayCommand OpenRepositoryCommand { get; } = new(() => launcher.Open(UpdateSource.RepositoryUrl));

    public RelayCommand OpenReleasesCommand { get; } = new(() => launcher.Open(UpdateSource.ReleasesPageUrl));
}
