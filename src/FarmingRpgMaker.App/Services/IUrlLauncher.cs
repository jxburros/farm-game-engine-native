using System.Diagnostics;

namespace FarmingRpgMaker.App.Services;

/// <summary>Opens web links in the user's browser.</summary>
public interface IUrlLauncher
{
    void Open(string url);
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

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
#pragma warning disable CA1031 // No browser configured: nothing sensible to do.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
