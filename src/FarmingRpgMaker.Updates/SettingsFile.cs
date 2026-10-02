using System.Text.Json;
using System.Text.Json.Nodes;

namespace FarmingRpgMaker.Updates;

/// <summary>
/// The shared <c>settings.json</c>: one top-level section per store (<c>"updates"</c> for the
/// Update Center, <c>"workspace"</c> for the editor). The one read-modify-write both stores use:
/// <list type="bullet">
/// <item>A file that exists but can't be read (locked by antivirus or a sync client, no
/// permission) is never overwritten: <see cref="WriteSection"/> throws instead, so a transient
/// error can't wipe the other store's section.</item>
/// <item>Every write keeps the previous file as <c>settings.json.bak</c>. A corrupt file is read
/// from that backup, and copied to <c>settings.json.corrupt</c> before the next write replaces it.</item>
/// <item>Writes are atomic and flushed to disk (<see cref="AtomicFile"/>).</item>
/// </list>
/// </summary>
public sealed class SettingsFile
{
    /// <summary>One lock for every instance: two stores in one process share the file.</summary>
    private static readonly Lock Gate = new();

    public SettingsFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>The previous version of the file, replaced on every write.</summary>
    public string BackupPath => FilePath + ".bak";

    /// <summary>A corrupt file, kept for inspection before a write replaces it.</summary>
    public string CorruptCopyPath => FilePath + ".corrupt";

    /// <summary>
    /// Section <paramref name="name"/>, or null when the file or the section is missing. A corrupt
    /// file is read from its backup. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the file exists but can't be read.
    /// </summary>
    public JsonNode? ReadSection(string name)
    {
        lock (Gate)
        {
            return ReadRoot(out _)?[name]?.DeepClone();
        }
    }

    /// <summary>
    /// Replaces section <paramref name="name"/> and keeps every other section. Throws, writing
    /// nothing, when the existing file can't be read; throws when the write fails.
    /// </summary>
    public void WriteSection(string name, JsonNode? value)
    {
        lock (Gate)
        {
            var root = ReadRoot(out var corrupt) ?? new JsonObject();
            if (corrupt)
            {
                File.Copy(FilePath, CorruptCopyPath, overwrite: true);
            }

            root[name] = value;
            // A corrupt file must not become the backup: the backup is what it was read from.
            AtomicFile.WriteAllText(FilePath, root.ToJsonString(JsonSettingsStore.JsonOptions), corrupt ? null : BackupPath);
        }
    }

    private JsonObject? ReadRoot(out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(FilePath))
        {
            return null;
        }

        // IO errors propagate: an unreadable file is not an empty one.
        if (Parse(File.ReadAllText(FilePath)) is { } root)
        {
            return root;
        }

        corrupt = true;
        System.Diagnostics.Trace.TraceWarning($"{FilePath} is not valid settings JSON; reading {BackupPath} instead.");
        try
        {
            return File.Exists(BackupPath) ? Parse(File.ReadAllText(BackupPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonObject? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
