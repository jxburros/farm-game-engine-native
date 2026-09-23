using System.Text.Json.Nodes;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.Tests.Updates;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "frm-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "FarmingRpgMaker", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var settings = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(new UpdateSettings(), settings);
        Assert.True(settings.CheckOnStartup);
        Assert.False(settings.AutoDownload);
        Assert.Equal(UpdateChannel.Stable, settings.Channel);
        Assert.Null(settings.LastChecked);
        Assert.Null(settings.SkippedVersion);
    }

    [Fact]
    public void RoundTrip_CreatesDirectoryAndPreservesValues()
    {
        var original = new UpdateSettings
        {
            CheckOnStartup = false,
            AutoDownload = true,
            Channel = UpdateChannel.Prerelease,
            LastChecked = new DateTimeOffset(2026, 9, 23, 8, 15, 0, TimeSpan.FromHours(-4)),
            SkippedVersion = "0.2.0",
        };

        new JsonSettingsStore(SettingsPath).Save(original);
        var loaded = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(original, loaded);
        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("prerelease", json["updates"]!["channel"]!.GetValue<string>());
        Assert.Equal("0.2.0", json["updates"]!["skippedVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Save_IsAtomic_NoTempFilesLeftAndOverwrites()
    {
        var store = new JsonSettingsStore(SettingsPath);
        store.Save(new UpdateSettings { SkippedVersion = "1.0.0" });
        store.Save(new UpdateSettings { SkippedVersion = "1.0.1" });

        Assert.Equal("1.0.1", store.Load().SkippedVersion);
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_PreservesOtherSections()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "recentProjects": ["a.json", "b.json"], "updates": { "autoDownload": true } }""");

        var store = new JsonSettingsStore(SettingsPath);
        Assert.True(store.Load().AutoDownload);
        store.Save(store.Load() with { Channel = UpdateChannel.Prerelease });

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(2, json["recentProjects"]!.AsArray().Count);
        Assert.Equal("prerelease", json["updates"]!["channel"]!.GetValue<string>());
        Assert.True(json["updates"]!["autoDownload"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{ "updates": { "channel": "nightly-unknown" } }""")]
    public void CorruptFile_FallsBackToDefaults_AndCanBeOverwritten(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, content);
        var store = new JsonSettingsStore(SettingsPath);

        Assert.Equal(new UpdateSettings(), store.Load());

        store.Save(new UpdateSettings { AutoDownload = true });
        Assert.True(store.Load().AutoDownload);
    }

    [Fact]
    public void DefaultPath_IsUnderApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "FarmingRpgMaker",
            "settings.json");
        Assert.Equal(expected, JsonSettingsStore.DefaultFilePath);
    }
}
