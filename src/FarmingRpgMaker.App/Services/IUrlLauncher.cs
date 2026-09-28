using System.Diagnostics;

namespace FarmingRpgMaker.App.Services;

/// <summary>Opens web links in the user's browser and folders in the file manager.</summary>
public interface IUrlLauncher
{
    void Open(string url);

    /// <summary>Shows a local folder in Explorer / the desktop's file manager.</summary>
    void OpenFolder(string path);
}

/// <summary>Uses the OS shell (<c>Process.Start</c> with <c>UseShellExecute</c>).</summary>
public sealed class ShellUrlLauncher : IUrlLauncher
{
    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        Start(uri.AbsoluteUri);
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Start(Path.GetFullPath(path));
    }

    private static void Start(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
#pragma warning disable CA1031 // No browser or file manager configured: nothing sensible to do.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
