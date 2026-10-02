using FarmingRpgMaker.App.Tests.Game;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.Tests.Updates;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllText_ReplacesTheFile_AndKeepsThePreviousVersionAsTheBackup()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "file.json");
        var backup = Path.Combine(dir.Path, "sub", "backups", "file.previous.json");

        AtomicFile.WriteAllText(path, "one", backup);
        Assert.False(File.Exists(backup), "nothing to back up on the first write");
        AtomicFile.WriteAllText(path, "two", backup);
        AtomicFile.WriteAllText(path, "three", backup);

        Assert.Equal("three", File.ReadAllText(path));
        Assert.Equal("two", File.ReadAllText(backup));
        Assert.Equal(["file.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void DeleteStaleTempFiles_RemovesOnlyOldLeftoversOfItsOwnWrites()
    {
        using var dir = new TempDir();
        var time = new TestTime(DateTimeOffset.UtcNow.AddHours(1));
        var stale = Path.Combine(dir.Path, $".proj-a.json.{Guid.NewGuid():N}.tmp");
        var other = Path.Combine(dir.Path, ".notes.tmp");
        var project = Path.Combine(dir.Path, "proj-a.json");
        foreach (var file in new[] { stale, other, project })
        {
            File.WriteAllText(file, "x");
        }

        // A write that is still in progress (younger than the minimum age) is left alone.
        Assert.Equal(0, AtomicFile.DeleteStaleTempFiles(dir.Path, TimeSpan.FromHours(2), time));
        Assert.Equal(1, AtomicFile.DeleteStaleTempFiles(dir.Path, TimeSpan.FromMinutes(10), time));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(project));
        Assert.Equal(0, AtomicFile.DeleteStaleTempFiles(Path.Combine(dir.Path, "missing"), TimeSpan.Zero));
    }
}
