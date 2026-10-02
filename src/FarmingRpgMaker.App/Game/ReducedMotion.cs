using System.Diagnostics;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Whether the operating system asks for less motion (web: <c>prefers-reduced-motion</c>), so a
/// playtest starts with the game's reduced-motion setting on like the web demo does. Read once:
/// Windows' "Animate controls and elements" (<c>MinAnimate</c>), macOS' "Reduce motion" and
/// GNOME's "Animations" switch. Anything unreadable counts as no preference.
/// </summary>
public static class ReducedMotion
{
    private static readonly Lazy<bool> Detected = new(Detect);

    /// <summary>True when the system asks for reduced motion.</summary>
    public static bool SystemPrefers => Detected.Value;

    private static bool Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", null) is string animate
                    && animate.Trim() == "0";
            }

            if (OperatingSystem.IsMacOS())
            {
                return Read("defaults", "read", "com.apple.universalaccess", "reduceMotion") == "1";
            }

            if (OperatingSystem.IsLinux())
            {
                return Read("gsettings", "get", "org.gnome.desktop.interface", "enable-animations") == "false";
            }
        }
#pragma warning disable CA1031 // A missing tool or setting means "no preference".
        catch (Exception error)
#pragma warning restore CA1031
        {
            Trace.TraceInformation($"Reading the reduced-motion setting failed: {error.Message}");
        }

        return false;
    }

    /// <summary>A settings tool's one-line answer, or null when it is missing, fails or takes too long.</summary>
    private static string? Read(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(1500))
        {
            process.Kill();
            return null;
        }

        return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
    }
}
