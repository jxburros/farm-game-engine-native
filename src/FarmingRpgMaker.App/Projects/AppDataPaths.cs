namespace FarmingRpgMaker.App.Projects;

/// <summary>Where the desktop app keeps its data.</summary>
public static class AppDataPaths
{
    /// <summary>Overrides the data directory (tests, portable installs).</summary>
    public const string DataDirectoryVariable = "FARMING_RPG_MAKER_DATA_DIR";

    /// <summary>
    /// <c>%APPDATA%/FarmingRpgMaker</c> (Linux/macOS: <c>~/.config/FarmingRpgMaker</c>), or
    /// <see cref="DataDirectoryVariable"/> when set. Holds <c>settings.json</c> and <c>projects/</c>.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                "FarmingRpgMaker");
        }
    }
}
