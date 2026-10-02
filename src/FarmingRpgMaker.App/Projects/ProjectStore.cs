using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.Updates;
using Microsoft.FSharp.Collections;

namespace FarmingRpgMaker.App.Projects;

/// <summary>When a project file was last written, and how big it is: a change by anything else shows.</summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length);

/// <summary>A row of the project list (web <c>ProjectIndexEntry</c> + file size).</summary>
public sealed record ProjectSummary(string Id, string Name, DateTimeOffset UpdatedAt, long SizeBytes);

/// <summary>Outcome of loading/importing a project: the migrated project or user-facing errors.</summary>
public sealed record ProjectLoadResult(GameProject? Project, IReadOnlyList<string> Errors, double? MigratedFrom = null)
{
    public bool Ok => Project is not null && Errors.Count == 0;

    public static ProjectLoadResult Fail(params string[] errors) => new(null, errors);
}

/// <summary>
/// Desktop project storage (web src/lib/projects.ts, with files instead of localStorage):
/// one web-compatible project JSON per project in <c>&lt;root&gt;/projects/&lt;id&gt;.json</c>
/// plus <c>index.json</c> (id, name, updated time). Every write is atomic and flushed to disk
/// (<see cref="AtomicFile"/>) and keeps the previous version in <c>backups/</c>; every load goes
/// through the F# project migrations (<see cref="ProjectMigrations"/>), and the first save after
/// a migration first copies the original file to <c>backups/&lt;id&gt;.v&lt;from&gt;.json</c>.
/// </summary>
/// <remarks>
/// The FILE is the source of truth for a project's id: <see cref="Load"/> hands back the project
/// under the id its file name stands for, whatever id the JSON inside carries (every web export
/// says <c>project-1</c>, and a hand-made copy repeats its original's id), so saving it writes
/// the file it came from.
/// </remarks>
public sealed class ProjectStore
{
    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    /// <summary>Loaded projects that were migrated, by id: the schema version their file still has.</summary>
    private readonly Dictionary<string, double> _migratedFiles = new(StringComparer.Ordinal);

    public ProjectStore(string rootDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = rootDirectory;
        _time = time ?? TimeProvider.System;
    }

    public string RootDirectory { get; }

    public string ProjectsDirectory => Path.Combine(RootDirectory, "projects");

    public string IndexPath => Path.Combine(ProjectsDirectory, IndexFileName);

    /// <summary>Previous versions of project files and pre-migration originals.</summary>
    public string BackupsDirectory => Path.Combine(ProjectsDirectory, "backups");

    /// <summary>File that holds project <paramref name="id"/> (<see cref="SafeFileName"/>).</summary>
    public string PathFor(string id) => Path.Combine(ProjectsDirectory, SafeFileName(id) + ".json");

    /// <summary>The version of project <paramref name="id"/> before its last save.</summary>
    public string PreviousVersionPath(string id) => Path.Combine(BackupsDirectory, SafeFileName(id) + ".previous.json");

    /// <summary>The file of project <paramref name="id"/> as it was before migrating from schema <paramref name="fromVersion"/>.</summary>
    public string MigrationBackupPath(string id, double fromVersion) =>
        Path.Combine(BackupsDirectory, $"{SafeFileName(id)}.v{fromVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}.json");

    public bool Exists(string id) => File.Exists(PathFor(id));

    /// <summary>The hidden lock file of project <paramref name="id"/> (<see cref="ProjectLock"/>).</summary>
    public string LockPathFor(string id) => Path.Combine(ProjectsDirectory, "." + SafeFileName(id) + ".json.lock");

    /// <summary>The project file's <see cref="FileStamp"/>, or null when it is missing or unreadable.</summary>
    public FileStamp? Stamp(string id)
    {
        try
        {
            var info = new FileInfo(PathFor(id));
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>All stored projects, most recently updated first.</summary>
    public IReadOnlyList<ProjectSummary> List()
    {
        lock (_gate)
        {
            var index = ReadIndex();
            var result = new List<ProjectSummary>();
            foreach (var entry in index.Projects)
            {
                var path = PathFor(entry.Id);
                if (!File.Exists(path))
                {
                    continue;
                }

                result.Add(new ProjectSummary(entry.Id, entry.Name, DateTimeOffset.FromUnixTimeMilliseconds(entry.UpdatedAt), new FileInfo(path).Length));
            }

            return result.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Loads and migrates project <paramref name="id"/>, under that id whatever the file's JSON
    /// says (see the remarks on <see cref="ProjectStore"/>). Never throws.
    /// </summary>
    public ProjectLoadResult Load(string id)
    {
        string json;
        try
        {
            json = File.ReadAllText(PathFor(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProjectLoadResult.Fail($"Could not read the project file: {ex.Message}");
        }

        var loaded = Parse(json);
        if (loaded.Project is { } project && project.Id != id)
        {
            loaded = loaded with { Project = project.WithId(id) };
        }

        if (loaded.Ok && loaded.MigratedFrom is { } from)
        {
            lock (_gate)
            {
                _migratedFiles[id] = from;
            }
        }

        return loaded;
    }

    /// <summary>
    /// Writes the project file (atomically, keeping the previous version in
    /// <see cref="BackupsDirectory"/>) and refreshes its index entry. The first save of a
    /// migrated project first keeps the original file (<see cref="MigrationBackupPath"/>).
    /// </summary>
    public void Save(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);
        lock (_gate)
        {
            var path = PathFor(project.Id);
            if (_migratedFiles.TryGetValue(project.Id, out var from))
            {
                // Never overwrite a file from an older schema without a copy: a lossy migration
                // can then still be undone by hand.
                var backup = MigrationBackupPath(project.Id, from);
                if (File.Exists(path) && !File.Exists(backup))
                {
                    Directory.CreateDirectory(BackupsDirectory);
                    File.Copy(path, backup);
                }

                _migratedFiles.Remove(project.Id);
            }

            AtomicFile.WriteAllText(path, ToJson(project), PreviousVersionPath(project.Id));
            var index = ReadIndex();
            var entry = new IndexEntry { Id = project.Id, Name = DisplayName(project), UpdatedAt = _time.GetUtcNow().ToUnixTimeMilliseconds() };
            var projects = index.Projects.Where(p => p.Id != project.Id).Append(entry).ToList();
            WriteIndex(new IndexFile { Projects = projects });
        }
    }

    /// <summary>
    /// Deletes project <paramref name="id"/>. Throws when the file can't be deleted (read-only,
    /// locked by another program) or the project is open in another editor window.
    /// </summary>
    public void Delete(string id)
    {
        if (ProjectLock.IsHeldElsewhere(this, id))
        {
            throw new IOException("It is open in another Farming RPG Maker window.");
        }

        lock (_gate)
        {
            var path = PathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(PreviousVersionPath(id)))
            {
                File.Delete(PreviousVersionPath(id));
            }

            var index = ReadIndex();
            WriteIndex(new IndexFile { Projects = index.Projects.Where(p => p.Id != id).ToList() });
        }
    }

    /// <summary>
    /// Project list "Rename" (web <c>renameProject</c>): rewrites the stored project with the new
    /// name. Blank names are refused. Never throws.
    /// </summary>
    public ProjectLoadResult Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProjectLoadResult.Fail("A project needs a name.");
        }

        var loaded = Load(id);
        if (!loaded.Ok)
        {
            return loaded;
        }

        var renamed = ProjectList.Rename(loaded.Project!, name);
        return TrySave(renamed);
    }

    /// <summary>
    /// Project list "Duplicate" (web <c>duplicateProject</c>): a copy under a fresh id, named
    /// "Name (copy)" unless <paramref name="name"/> is given. Never throws.
    /// </summary>
    public ProjectLoadResult Duplicate(string id, string? name = null)
    {
        var loaded = Load(id);
        if (!loaded.Ok)
        {
            return loaded;
        }

        return TrySave(ProjectList.Duplicate(loaded.Project!, NewId(), name));
    }

    private ProjectLoadResult TrySave(GameProject project)
    {
        try
        {
            Save(project);
            return new ProjectLoadResult(project, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProjectLoadResult.Fail($"Could not write the project file: {ex.Message}");
        }
    }

    /// <summary>A fresh unique project id (web <c>proj-&lt;base36 time&gt;</c>).</summary>
    public string NewId()
    {
        var now = (double)_time.GetUtcNow().ToUnixTimeMilliseconds();
        var id = ProjectCatalog.NewProjectId(now);
        while (Exists(id))
        {
            now++;
            id = ProjectCatalog.NewProjectId(now);
        }

        return id;
    }

    /// <summary>Web-compatible project JSON (indented, camelCase, like the web export).</summary>
    public static string ToJson(GameProject project) => ProjectLoad.toText(project);

    /// <summary>Parses + migrates stored project JSON. Never throws.</summary>
    public static ProjectLoadResult Parse(string json)
    {
        var result = ProjectMigrations.migrateProjectText(json);
        return result.Ok && result.Data.OrNull() is { } data
            ? new ProjectLoadResult(data, [], result.Migrated ? result.FromVersion : null)
            : ProjectLoadResult.Fail(result.Errors.Length > 0 ? [.. result.Errors] : ["Invalid project data"]);
    }

    /// <summary>
    /// Imports project/exported-game JSON as a NEW project (web <c>importProjectFromData</c>):
    /// full project files migrate as projects; exported games are layered over a blank
    /// project. Editor-only state starts fresh. The caller saves the result.
    /// </summary>
    public ProjectLoadResult Import(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return ProjectLoadResult.Fail($"The file is not valid JSON: {ex.Message}");
        }

        if (node is not JsonObject)
        {
            return ProjectLoadResult.Fail("The file does not contain a project (expected a JSON object).");
        }

        var asProject = ProjectMigrations.migrateProject(node);
        GameProject project;
        double? migratedFrom;
        if (asProject.Ok && asProject.Data.OrNull() is { } loaded)
        {
            project = loaded;
            migratedFrom = asProject.Migrated ? asProject.FromVersion : null;
        }
        else
        {
            var exported = ProjectMigrations.migrateExportedGame(node);
            if (!exported.Ok || exported.Data.OrNull() is not { } game)
            {
                var errors = asProject.Errors.Length > 0 ? asProject.Errors : exported.Errors;
                return ProjectLoadResult.Fail(errors.Length > 0 ? [.. errors] : ["Invalid project data"]);
            }

            // { ...createBlankProject(), ...exportedGame } at the JSON level, then re-validate.
            var merged = RecordJson.ToNode(ProjectCatalog.CreateBlankProject(_time.GetUtcNow().ToUnixTimeMilliseconds())).AsObject();
            var data = RecordJson.ToNode(game).AsObject();
            foreach (var (key, value) in data)
            {
                merged[key] = value?.DeepClone();
            }

            merged["schemaVersion"] = ProjectSchema.CurrentProjectSchemaVersion;
            var reparsed = ProjectMigrations.migrateProject(merged);
            if (!reparsed.Ok || reparsed.Data.OrNull() is not { } imported)
            {
                return ProjectLoadResult.Fail(reparsed.Errors.Length > 0 ? [.. reparsed.Errors] : ["Invalid project data"]);
            }

            project = imported;
            migratedFrom = exported.Migrated ? exported.FromVersion : null;
        }

        var name = string.IsNullOrWhiteSpace(project.Name) ? "Imported Game" : project.Name.Trim();
        project = project.WithId(NewId()).WithName(name).WithMode("tiles").WithSelectedTileType("grass").WithSelectedNpcId(null).WithSelectedItemId(null).WithEventFlags(FSharpList<Tuple<string, FarmEngine.Authoring.Json>>.Empty);
        return new ProjectLoadResult(project, [], migratedFrom);
    }

    private static string DisplayName(GameProject project) => string.IsNullOrWhiteSpace(project.Name) ? "Untitled Game" : project.Name;

    /// <summary>
    /// Deletes temp files a killed editor left in the projects folder (<see cref="AtomicFile.DeleteStaleTempFiles"/>).
    /// Never throws.
    /// </summary>
    public int DeleteStaleTempFiles() => AtomicFile.DeleteStaleTempFiles(ProjectsDirectory, TimeSpan.FromMinutes(10), _time);

    /// <summary>
    /// Device names Windows refuses as a file name, with or without an extension (and after
    /// trailing dots and spaces are dropped).
    /// </summary>
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// The file stem for a project id, the same on every platform and one-to-one (two ids never
    /// share a file): characters Windows refuses in a file name, <c>%</c> itself, a leading dot
    /// and trailing dots or spaces are escaped as <c>%XX</c> (UTF-8 bytes), and so is the first
    /// letter of a Windows device name (<c>con</c> becomes <c>%63on</c>). Ordinary ids
    /// (<c>proj-abc</c>, <c>project-1</c>) are their own file names.
    /// </summary>
    internal static string SafeFileName(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 0)
        {
            return "%";
        }

        var reserved = WindowsReservedNames.Contains(id.Split('.')[0].TrimEnd(' ', '.'));
        var builder = new System.Text.StringBuilder(id.Length);
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            var escape = c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '%'
                || (i == 0 && (c == '.' || reserved))
                || (c is '.' or ' ' && id.AsSpan(i).TrimEnd(". ").IsEmpty);
            if (!escape)
            {
                builder.Append(c);
                continue;
            }

            // A surrogate pair is escaped as one character (its four UTF-8 bytes).
            var length = char.IsHighSurrogate(c) && i + 1 < id.Length && char.IsLowSurrogate(id[i + 1]) ? 2 : 1;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(id.Substring(i, length)))
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            i += length - 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The id whose <see cref="SafeFileName"/> is <paramref name="stem"/>, or null when no id maps
    /// to it (a hand-made file name such as <c>100% farm</c> or <c>con</c>).
    /// </summary>
    internal static string? IdForFileName(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var bytes = new List<byte>();
        var text = new System.Text.StringBuilder(stem.Length);
        for (var i = 0; i < stem.Length; i++)
        {
            if (stem[i] == '%' && i + 2 < stem.Length
                && byte.TryParse(stem.AsSpan(i + 1, 2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
                continue;
            }

            DecodeBytes();
            text.Append(stem[i]);
        }

        DecodeBytes();
        var id = text.ToString();
        return id.Length > 0 && SafeFileName(id) == stem ? id : null;

        void DecodeBytes()
        {
            if (bytes.Count > 0)
            {
                text.Append(System.Text.Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }
        }
    }

    private IndexFile ReadIndex()
    {
        IndexFile? index = null;
        try
        {
            if (File.Exists(IndexPath))
            {
                index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(IndexPath), IndexOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            index = null;
        }

        index ??= new IndexFile();

        // Adopt project files the index doesn't know (copied in by hand, or a lost index).
        if (Directory.Exists(ProjectsDirectory))
        {
            var known = index.Projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var adopted = new List<IndexEntry>();
            foreach (var file in Directory.EnumerateFiles(ProjectsDirectory, "*.json"))
            {
                var fileName = Path.GetFileName(file);
                if (fileName == IndexFileName || fileName.StartsWith('.'))
                {
                    continue;
                }

                if (index.Projects.Any(p => PathFor(p.Id) == file))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(file);
                var path = file;
                if (IdForFileName(stem) is not { } id)
                {
                    // A name no id maps to ("100% farm.json"): the file takes its id's name.
                    id = stem;
                    path = PathFor(id);
                    if (!TryRename(file, path))
                    {
                        continue;
                    }
                }

                if (known.Contains(id))
                {
                    continue;
                }

                adopted.Add(new IndexEntry { Id = id, Name = PeekName(path) ?? id, UpdatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() });
            }

            if (adopted.Count > 0)
            {
                index = new IndexFile { Projects = [.. index.Projects, .. adopted] };
            }
        }

        return index;
    }

    private void WriteIndex(IndexFile index) => AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(index, IndexOptions));

    private static bool TryRename(string from, string to)
    {
        try
        {
            if (File.Exists(to))
            {
                return false;
            }

            File.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not adopt the project file {from}: {ex.Message}");
            return false;
        }
    }

    private static string? PeekName(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record IndexFile
    {
        public List<IndexEntry> Projects { get; init; } = [];
    }

    private sealed record IndexEntry
    {
        public string Id { get; init; } = "";

        public string Name { get; init; } = "";

        public long UpdatedAt { get; init; }
    }
}
