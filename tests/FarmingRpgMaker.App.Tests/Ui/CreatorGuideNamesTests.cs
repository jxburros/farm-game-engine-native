using System.Text.RegularExpressions;
using FarmingRpgMaker.App.Tests.Interop;
using FarmingRpgMaker.App.Views;

namespace FarmingRpgMaker.App.Tests.Ui;

/// <summary>
/// The Creator Guide is also the in-app help, so the button, tab and menu names it puts in bold must be
/// labels the editor (or the game it plays) actually shows.
/// </summary>
public sealed partial class CreatorGuideNamesTests
{
    // Bold text that is a key, a game term or emphasis, not a label on screen.
    private static readonly HashSet<string> NotUiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+Y",
        "arrow keys",
        "animation clips",
        "Errors block export",
        "pauses",
    };

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A string literal in C#, F#, Rust or an XAML attribute.
    [GeneratedRegex(@"""((?:[^""\\\n]|\\.)*)""")]
    private static partial Regex StringLiteral();

    /// <summary>Every string literal of the editor, the authoring core, Export Game and the game UI, as a label.</summary>
    private static HashSet<string> Labels()
    {
        var roots = new[]
        {
            (Path: RustSessionTests.RepoFile("src", "FarmingRpgMaker.App"), Patterns: new[] { "*.cs", "*.axaml" }),
            (Path: RustSessionTests.RepoFile("src", "FarmEngine.Authoring"), Patterns: new[] { "*.fs" }),
            (Path: RustSessionTests.RepoFile("src", "FarmEngine.Export"), Patterns: new[] { "*.fs" }),
            // The game's own menus (Settings → Accessibility …) are drawn by the Rust UI.
            (Path: RustSessionTests.RepoFile("crates", "farm-ui", "src"), Patterns: new[] { "*.rs" }),
        };
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, patterns) in roots)
        {
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories))
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
                    {
                        // "_Open Project…" is the label "Open Project".
                        labels.Add(literal.Groups[1].Value.Replace("_", "", StringComparison.Ordinal).TrimEnd('…', '.').Trim());
                    }
                }
            }
        }

        return labels;
    }

    [Fact]
    public void BoldUiNames_AreLabelsOfTheEditorOrGame()
    {
        var labels = Labels();
        var missing = Bold().Matches(HelpContent.CreatorGuideMarkdown())
            .Select(match => Whitespace().Replace(match.Groups[1].Value, " ").Trim())
            // "Help → Update Center" names a menu and its item; each part is a label of its own.
            .SelectMany(name => name.Split(" → ", StringSplitOptions.TrimEntries))
            .Where(name => !NotUiNames.Contains(name) && !labels.Contains(name))
            .Distinct()
            .ToList();
        Assert.True(missing.Count == 0, "Not a label in the editor or game: " + string.Join(", ", missing));
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
