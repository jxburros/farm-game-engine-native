using System.Text.RegularExpressions;
using FarmingRpgMaker.App.Tests.Interop;
using FarmingRpgMaker.App.Views;

namespace FarmingRpgMaker.App.Tests.Ui;

/// <summary>
/// The Creator Guide is also the in-app help, so the button, tab and menu names it puts in bold must be
/// names the editor (or the game it plays) actually shows.
/// </summary>
public sealed partial class CreatorGuideNamesTests
{
    // Bold text that is a game term or a key name, not a label on screen.
    private static readonly HashSet<string> NotUiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "animation clips",
    };

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static string Sources()
    {
        var roots = new[]
        {
            (Path: RustSessionTests.RepoFile("src", "FarmingRpgMaker.App"), Patterns: new[] { "*.cs", "*.axaml" }),
            (Path: RustSessionTests.RepoFile("src", "FarmEngine.Authoring"), Patterns: new[] { "*.fs" }),
            (Path: RustSessionTests.RepoFile("src", "FarmEngine.Export"), Patterns: new[] { "*.fs" }),
            // The game's own menus (Settings → Accessibility …) are drawn by the Rust UI.
            (Path: RustSessionTests.RepoFile("crates", "farm-ui", "src"), Patterns: new[] { "*.rs" }),
        };
        var text = new System.Text.StringBuilder();
        foreach (var (path, patterns) in roots)
        {
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories))
                {
                    text.AppendLine(File.ReadAllText(file));
                }
            }
        }

        return text.ToString();
    }

    [Fact]
    public void BoldUiNames_AppearInTheEditorOrGameSources()
    {
        var guide = HelpContent.CreatorGuideMarkdown();
        var sources = Sources();
        var missing = Bold().Matches(guide)
            .Select(match => Whitespace().Replace(match.Groups[1].Value, " ").Trim())
            .Where(name => !NotUiNames.Contains(name))
            // "Help → Update Center" names a menu and its item; each part is a label of its own.
            .SelectMany(name => name.Split(" → ", StringSplitOptions.TrimEntries))
            .Distinct()
            .Where(name => !sources.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(missing.Count == 0, "Not found in the editor or game: " + string.Join(", ", missing));
    }

    [Fact]
    public void GuideLinks_OpenTheDocsOfTheInstalledRelease()
    {
        const string Repo = "https://github.com/jxburros/farm-game-engine-native/blob/";
        Assert.Equal(Repo + "v0.3.0/docs/", HelpContent.DocsBaseUrlFor("0.3.0"));
        Assert.Equal(Repo + "v0.4.0-beta.1/docs/", HelpContent.DocsBaseUrlFor("0.4.0-beta.1"));
        Assert.Equal(Repo + "main/docs/", HelpContent.DocsBaseUrlFor("0.3.0-dev"));
        Assert.Equal(Repo + "main/docs/", HelpContent.DocsBaseUrlFor("0.0.0"));
    }
}
