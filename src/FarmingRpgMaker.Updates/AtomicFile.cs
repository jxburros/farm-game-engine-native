using System.Text;
using System.Text.RegularExpressions;

namespace FarmingRpgMaker.Updates;

/// <summary>
/// Durable file replacement, shared by the editor's project store and both settings stores:
/// the new text goes to a temp file next to the target and is flushed to disk, then one
/// rename puts it in place, optionally keeping the previous version as a backup. A crash, a
/// power loss or a full disk leaves the old file or the new one, never a torn or empty one.
/// </summary>
public static partial class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="contents"/> (UTF-8) to <paramref name="path"/>. With
    /// <paramref name="backupPath"/>, the version being replaced moves there (one previous
    /// version, replaced on every write).
    /// </summary>
    public static void WriteAllText(string path, string contents, string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8NoBom.GetBytes(contents));
                // Without this the rename can reach the disk before the data does (NTFS and ext4
                // journal metadata, not contents): a power cut would leave an empty file.
                stream.Flush(flushToDisk: true);
            }

            if (backupPath is not null && File.Exists(fullPath))
            {
                var fullBackup = Path.GetFullPath(backupPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullBackup)!);
                File.Replace(tempPath, fullPath, fullBackup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Deletes temp files a killed process left behind in <paramref name="directory"/>: only
    /// <see cref="WriteAllText"/>'s own <c>.name.guid.tmp</c> files, and only those older than
    /// <paramref name="minimumAge"/>, so a write in progress elsewhere is never touched.
    /// Returns how many were deleted. Never throws.
    /// </summary>
    public static int DeleteStaleTempFiles(string directory, TimeSpan minimumAge, TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var deleted = 0;
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(directory, ".*.tmp"))
            {
                if (!TempFileName().IsMatch(Path.GetFileName(file)) || now - File.GetLastWriteTimeUtc(file) < minimumAge)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning($"Could not delete the stale temp file {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not clean up temp files in {directory}: {ex.Message}");
        }

        return deleted;
    }

    [GeneratedRegex(@"^\..+\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TempFileName();
}
