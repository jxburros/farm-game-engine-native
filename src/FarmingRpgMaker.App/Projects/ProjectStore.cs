using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Content;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Projects;

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
/// plus <c>index.json</c> (id, name, updated time). Every write is atomic (temp file +
/// move), and every load goes through <see cref="Migrations.MigrateProject(JsonNode?)"/>.
/// </summary>
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

    public ProjectStore(string rootDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = rootDirectory;
        _time = time ?? TimeProvider.System;
    }

    public string RootDirectory { get; }

    public string ProjectsDirectory => Path.Combine(RootDirectory, "projects");

    public string IndexPath => Path.Combine(ProjectsDirectory, IndexFileName);

    /// <summary>File that holds project <paramref name="id"/> (ids are sanitized to safe file names).</summary>
    public string PathFor(string id) => Path.Combine(ProjectsDirectory, SafeFileName(id) + ".json");

    public bool Exists(string id) => File.Exists(PathFor(id));

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

    /// <summary>Loads and migrates project <paramref name="id"/>. Never throws.</summary>
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

        return Parse(json);
    }

    /// <summary>Writes the project file (atomically) and refreshes its index entry.</summary>
    public void Save(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);
        lock (_gate)
        {
            AtomicFile.WriteAllText(PathFor(project.Id), ToJson(project));
            var index = ReadIndex();
            var entry = new IndexEntry { Id = project.Id, Name = DisplayName(project), UpdatedAt = _time.GetUtcNow().ToUnixTimeMilliseconds() };
            var projects = index.Projects.Where(p => p.Id != project.Id).Append(entry).ToList();
            WriteIndex(new IndexFile { Projects = projects });
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var path = PathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var index = ReadIndex();
            WriteIndex(new IndexFile { Projects = index.Projects.Where(p => p.Id != id).ToList() });
        }
    }

    /// <summary>A fresh unique project id (web <c>proj-&lt;base36 time&gt;</c>).</summary>
    public string NewId()
    {
        var now = (double)_time.GetUtcNow().ToUnixTimeMilliseconds();
        var id = Templates.NewProjectId(now);
        while (Exists(id))
        {
            now++;
            id = Templates.NewProjectId(now);
        }

        return id;
    }

    /// <summary>Web-compatible project JSON (indented, camelCase, like the web export).</summary>
    public static string ToJson(GameProject project) => JsonDefaults.Serialize(project, indented: true);

    /// <summary>Parses + migrates stored project JSON. Never throws.</summary>
    public static ProjectLoadResult Parse(string json)
    {
        var result = Migrations.MigrateProject(json);
        return result.Ok && result.Data is not null
            ? new ProjectLoadResult(result.Data, [], result.Migrated ? result.FromVersion : null)
            : ProjectLoadResult.Fail(result.Errors.Count > 0 ? [.. result.Errors] : ["Invalid project data"]);
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

        var asProject = Migrations.MigrateProject(node);
        GameProject project;
        double? migratedFrom;
        if (asProject.Ok && asProject.Data is not null)
        {
            project = asProject.Data;
            migratedFrom = asProject.Migrated ? asProject.FromVersion : null;
        }
        else
        {
            var exported = Migrations.MigrateExportedGame(node);
            if (!exported.Ok || exported.Data is null)
            {
                var errors = asProject.Errors.Count > 0 ? asProject.Errors : exported.Errors;
                return ProjectLoadResult.Fail(errors.Count > 0 ? [.. errors] : ["Invalid project data"]);
            }

            // { ...createBlankProject(), ...exportedGame } at the JSON level, then re-validate.
            var merged = JsonSerializer.SerializeToNode(DefaultContent.CreateBlankProject(_time.GetUtcNow().ToUnixTimeMilliseconds()), JsonDefaults.Options)!.AsObject();
            var data = JsonSerializer.SerializeToNode(exported.Data, JsonDefaults.Options)!.AsObject();
            foreach (var (key, value) in data)
            {
                merged[key] = value?.DeepClone();
            }

            merged["schemaVersion"] = ProjectSchema.CurrentProjectSchemaVersion;
            var reparsed = Migrations.MigrateProject(merged);
            if (!reparsed.Ok || reparsed.Data is null)
            {
                return ProjectLoadResult.Fail(reparsed.Errors.Count > 0 ? [.. reparsed.Errors] : ["Invalid project data"]);
            }

            project = reparsed.Data;
            migratedFrom = exported.Migrated ? exported.FromVersion : null;
        }

        var name = string.IsNullOrWhiteSpace(project.Name) ? "Imported Game" : project.Name.Trim();
        project = project with
        {
            Id = NewId(),
            Name = name,
            Mode = "tiles",
            SelectedTileType = "grass",
            SelectedNpcId = null,
            SelectedItemId = null,
            EventFlags = [],
        };
        return new ProjectLoadResult(project, [], migratedFrom);
    }

    private static string DisplayName(GameProject project) => string.IsNullOrWhiteSpace(project.Name) ? "Untitled Game" : project.Name;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A file stem for a project id: invalid characters replaced, and Windows device names
    /// (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, …, which Windows refuses even with an extension)
    /// suffixed so the file can be created on every platform.
    /// </summary>
    internal static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(id.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray()).Trim('.', ' ');
        if (string.IsNullOrEmpty(safe))
        {
            return "project";
        }

        var stem = safe.Split('.')[0];
        return WindowsReservedNames.Contains(stem) ? safe + "_" : safe;
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

                var id = Path.GetFileNameWithoutExtension(file);
                if (known.Contains(id) || index.Projects.Any(p => PathFor(p.Id) == file))
                {
                    continue;
                }

                adopted.Add(new IndexEntry { Id = id, Name = PeekName(file) ?? id, UpdatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds() });
            }

            if (adopted.Count > 0)
            {
                index = new IndexFile { Projects = [.. index.Projects, .. adopted] };
            }
        }

        return index;
    }

    private void WriteIndex(IndexFile index) => AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(index, IndexOptions));

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
