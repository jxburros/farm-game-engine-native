using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>A pack load/merge problem for the Problems panel (TS <c>PackProblem</c>).</summary>
/// <param name="Severity">'error' | 'warning'.</param>
public sealed record PackProblem(string PackId, string Severity, string Message);

/// <summary>
/// Content pack loading (M5, port of packs.ts): deterministic load order,
/// <c>packId:localId</c> namespacing, explicit override semantics. Conflicts
/// surface as problems for the Problems panel — never silent last-wins.
/// </summary>
public static class Packs
{
    /// <summary>Namespace a local id into a pack: ids that already contain ':' are absolute.</summary>
    public static string NamespacedId(string packId, string id) => id.Contains(':') ? id : $"{packId}:{id}";

    /// <summary>The pack prefix of a namespaced id, or null for plain (base) ids.</summary>
    public static string? PackIdOf(string id)
    {
        var index = id.IndexOf(':');
        return index > 0 ? id[..index] : null;
    }

    /// <summary>
    /// Object keys that hold references to other definitions. During namespacing
    /// these are rewritten only when the value refers to an id defined in the
    /// same pack — references to base/global content must stay untouched, and
    /// cross-pack references must be written fully qualified by the author.
    /// </summary>
    private static readonly HashSet<string> ReferenceKeys =
    [
        "itemId", "cropType", "machineTypeId", "feedItemId", "productItemId",
        "npcId", "dialogueId", "nextDialogueId", "openShopId", "offerQuestId",
        "questId", "targetNPCId", "targetCropType", "shopId", "giver", "typeId",
        "speciesId", "recipeId", "nodeTypeId", "weatherId", "targetItemId",
        "requiredItemId", "giftItemId", "actionId", "minigameId", "useActionId",
    ];

    /// <summary>Keys holding arrays of reference strings.</summary>
    private static readonly HashSet<string> ReferenceListKeys = ["prerequisites"];

    private static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String)
        {
            value = jsonValue.GetValue<string>();
            return true;
        }
        value = "";
        return false;
    }

    private static JsonNode? RewriteReferences(JsonNode? value, string packId, HashSet<string> localIds)
    {
        if (value is JsonArray array)
        {
            return new JsonArray(array.Select(entry => RewriteReferences(entry, packId, localIds)).ToArray());
        }
        if (value is not JsonObject source) return value?.DeepClone();
        var result = new JsonObject();
        foreach (var (key, entry) in source)
        {
            if (ReferenceKeys.Contains(key) && TryGetString(entry, out var reference) && localIds.Contains(reference))
            {
                result[key] = NamespacedId(packId, reference);
            }
            else if (ReferenceListKeys.Contains(key) && entry is JsonArray list)
            {
                result[key] = new JsonArray(list
                    .Select(v => TryGetString(v, out var s) && localIds.Contains(s) ? (JsonNode?)JsonValue.Create(NamespacedId(packId, s)) : v?.DeepClone())
                    .ToArray());
            }
            else
            {
                result[key] = RewriteReferences(entry, packId, localIds);
            }
        }
        return result;
    }

    /// <summary>
    /// Rewrite a typed definition through its JSON shape (TS operates on plain
    /// objects), optionally namespacing its top-level <c>id</c>.
    /// </summary>
    private static T RewriteDefinition<T>(T definition, string packId, HashSet<string> localIds, bool namespaceId)
    {
        var rewritten = RewriteReferences(JsonSerializer.SerializeToNode(definition, JsonDefaults.Options), packId, localIds);
        if (namespaceId && rewritten is JsonObject obj && TryGetString(obj["id"], out var id))
        {
            obj["id"] = NamespacedId(packId, id);
        }
        return rewritten.Deserialize<T>(JsonDefaults.Options)!;
    }

    private static List<T> RewriteCollection<T>(List<T>? definitions, string packId, HashSet<string> localIds)
    {
        if (definitions is not { Count: > 0 }) return definitions!;
        return definitions.Select(definition => RewriteDefinition(definition, packId, localIds, namespaceId: true)).ToList();
    }

    /// <summary>Definition ids per collection, in TS <c>CONTENT_COLLECTIONS</c> order.</summary>
    private static IEnumerable<string?> CollectionIds(PackContent content) =>
        (content.Crops ?? []).Select(d => d.Id)
            .Concat((content.Items ?? []).Select(d => d.Id))
            .Concat((content.Recipes ?? []).Select(d => d.Id))
            .Concat((content.MachineTypes ?? []).Select(d => d.Id))
            .Concat((content.NodeTypes ?? []).Select(d => d.Id))
            .Concat((content.AnimalSpecies ?? []).Select(d => d.Id))
            .Concat((content.FishTables ?? []).Select(d => d.Id))
            .Concat((content.WeatherTypes ?? []).Select(d => d.Id))
            .Concat((content.Npcs ?? []).Select(d => d.Id))
            .Concat((content.Dialogues ?? []).Select(d => d.Id))
            .Concat((content.Scenes ?? []).Select(d => d.Id))
            .Concat((content.Events ?? []).Select(d => d.Id))
            .Concat((content.Quests ?? []).Select(d => d.Id))
            .Concat((content.Shops ?? []).Select(d => d.Id))
            .Concat((content.Actions ?? []).Select(d => d.Id))
            .Concat((content.Minigames ?? []).Select(d => d.Id));

    private static HashSet<string> CollectLocalIds(ContentPack pack)
    {
        var ids = new HashSet<string>();
        foreach (var id in CollectionIds(pack.Content))
        {
            if (id is not null && !id.Contains(':')) ids.Add(id);
        }
        // NPC dialogue arrays embed dialogues with their own ids.
        foreach (var npc in pack.Content.Npcs ?? [])
        {
            foreach (var dialogue in npc.Dialogue ?? [])
            {
                if (dialogue.Id is not null && !dialogue.Id.Contains(':')) ids.Add(dialogue.Id);
            }
        }
        return ids;
    }

    /// <summary>
    /// Return a copy of the pack with every definition id and intra-pack
    /// reference rewritten to <c>packId:localId</c>. Base packs pass through as-is.
    /// </summary>
    public static ContentPack NamespacePack(ContentPack pack)
    {
        if (pack.Manifest.Base) return pack;
        var packId = pack.Manifest.Id;
        var localIds = CollectLocalIds(pack);

        var source = pack.Content;
        var content = source with
        {
            Crops = RewriteCollection(source.Crops, packId, localIds),
            Items = RewriteCollection(source.Items, packId, localIds),
            Recipes = RewriteCollection(source.Recipes, packId, localIds),
            MachineTypes = RewriteCollection(source.MachineTypes, packId, localIds),
            NodeTypes = RewriteCollection(source.NodeTypes, packId, localIds),
            AnimalSpecies = RewriteCollection(source.AnimalSpecies, packId, localIds),
            FishTables = RewriteCollection(source.FishTables, packId, localIds),
            WeatherTypes = RewriteCollection(source.WeatherTypes, packId, localIds),
            Npcs = RewriteCollection(source.Npcs, packId, localIds),
            Dialogues = RewriteCollection(source.Dialogues, packId, localIds),
            Scenes = RewriteCollection(source.Scenes, packId, localIds),
            Events = RewriteCollection(source.Events, packId, localIds),
            Quests = RewriteCollection(source.Quests, packId, localIds),
            Shops = RewriteCollection(source.Shops, packId, localIds),
            Actions = RewriteCollection(source.Actions, packId, localIds),
            Minigames = RewriteCollection(source.Minigames, packId, localIds),
        };
        if (content.PlayerStart is not null)
        {
            content = content with { PlayerStart = RewriteDefinition(content.PlayerStart, packId, localIds, namespaceId: false) };
        }
        // String-table keys embed ids (`item:{id}:name`) — namespace those too.
        if (content.Strings is { Count: > 0 })
        {
            var strings = new OrderedDictionary<string, OrderedDictionary<string, string>>();
            foreach (var (locale, table) in content.Strings)
            {
                var rewritten = new OrderedDictionary<string, string>();
                foreach (var (key, value) in table)
                {
                    var parts = key.Split(':');
                    if (parts.Length >= 3 && localIds.Contains(string.Join(':', parts[1..^1])))
                    {
                        rewritten[$"{parts[0]}:{NamespacedId(packId, string.Join(':', parts[1..^1]))}:{parts[^1]}"] = value;
                    }
                    else
                    {
                        rewritten[key] = value;
                    }
                }
                strings[locale] = rewritten;
            }
            content = content with { Strings = strings };
        }
        return pack with { Content = content };
    }

    /// <summary>
    /// Apply per-locale string tables from enabled packs to game content (M7
    /// i18n). Authored text is the fallback: unknown locales and missing keys
    /// change nothing. Later packs override earlier ones, mirroring load order.
    /// </summary>
    public static GameContent ApplyLocaleStrings(GameContent content, List<PackInstallation> installs, string locale)
    {
        if (string.IsNullOrEmpty(locale)) return content;
        var packs = ResolvePackOrder(installs ?? []).Packs;
        var table = new OrderedDictionary<string, string>();
        foreach (var rawPack in packs)
        {
            var pack = NamespacePack(rawPack);
            if (pack.Content.Strings is { } strings && strings.TryGetValue(locale, out var localeTable) && localeTable is not null)
            {
                foreach (var (key, value) in localeTable) table[key] = value;
            }
        }
        if (table.Count == 0) return content;

        string? Lookup(string kind, string id, string field) =>
            table.TryGetValue($"{kind}:{id}:{field}", out var text) ? text : null;

        return content with
        {
            Items = content.Items.Select(item => item with
            {
                Name = Lookup("item", item.Id, "name") ?? item.Name,
                Description = Lookup("item", item.Id, "description") ?? item.Description,
            }).ToList(),
            Quests = content.Quests.Select(quest => quest with
            {
                Name = Lookup("quest", quest.Id, "name") ?? quest.Name,
                Description = Lookup("quest", quest.Id, "description") ?? quest.Description,
            }).ToList(),
            Dialogues = content.Dialogues.Select(dialogue => dialogue with
            {
                Text = Lookup("dialogue", dialogue.Id, "text") ?? dialogue.Text,
            }).ToList(),
            Npcs = content.Npcs.Select(npc => npc with
            {
                Name = Lookup("npc", npc.Id, "name") ?? npc.Name,
                Dialogue = (npc.Dialogue ?? []).Select(dialogue => dialogue with
                {
                    Text = Lookup("dialogue", dialogue.Id, "text") ?? dialogue.Text,
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>
    /// Deterministic load order: install order, but a pack's dependencies always
    /// load before it. Missing dependencies and cycles surface as problems (the
    /// pack still loads so partial setups stay inspectable).
    /// </summary>
    public static ResolvePackOrderResult ResolvePackOrder(List<PackInstallation> installs)
    {
        var problems = new List<PackProblem>();
        var enabled = installs.Where(install => install.Enabled).Select(install => install.Pack).ToList();
        // `new Map(entries)`: a later duplicate id replaces the earlier value.
        var byId = new Dictionary<string, ContentPack>();
        foreach (var pack in enabled) byId[pack.Manifest.Id] = pack;

        var ordered = new List<ContentPack>();
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        void Visit(ContentPack pack)
        {
            var id = pack.Manifest.Id;
            if (done.Contains(id)) return;
            if (visiting.Contains(id))
            {
                problems.Add(new PackProblem(id, "error", $"Dependency cycle involving pack '{id}'"));
                return;
            }
            visiting.Add(id);
            foreach (var dependency in pack.Manifest.Dependencies ?? [])
            {
                if (!byId.TryGetValue(dependency.PackId, out var target))
                {
                    problems.Add(new PackProblem(
                        id,
                        "error",
                        $"Pack '{id}' depends on '{dependency.PackId}' which is not installed/enabled"));
                    continue;
                }
                Visit(target);
            }
            visiting.Remove(id);
            done.Add(id);
            ordered.Add(pack);
        }

        foreach (var pack in enabled) Visit(pack);

        foreach (var pack in ordered)
        {
            if (!PacksSchema.IsEngineCompatible(pack.Manifest.EngineCompatibility))
            {
                problems.Add(new PackProblem(
                    pack.Manifest.Id,
                    "warning",
                    $"Pack '{pack.Manifest.Id}' targets engine {pack.Manifest.EngineCompatibility}; this engine is {PacksSchema.EngineVersion}"));
            }
        }

        return new ResolvePackOrderResult(ordered, problems);
    }

    /// <summary>TS <c>MergeTarget</c>: where a pack collection's definitions land.</summary>
    private sealed record MergeTarget<T>(Func<string, bool> Get, Action<T> Set, Action<T> Replace);

    private static void MergeCollection<T>(
        ContentPack pack,
        List<T> definitions,
        Func<T, string> idOf,
        MergeTarget<T> target,
        HashSet<string> overrides,
        List<PackProblem> problems,
        string label)
    {
        foreach (var definition in definitions)
        {
            var id = idOf(definition);
            if (!target.Get(id))
            {
                target.Set(definition);
            }
            else if (overrides.Contains(id))
            {
                target.Replace(definition);
            }
            else
            {
                problems.Add(new PackProblem(
                    pack.Manifest.Id,
                    "warning",
                    $"Pack '{pack.Manifest.Id}' redefines {label} '{id}' without declaring it in manifest.overrides — keeping the earlier definition"));
            }
        }
    }

    private static MergeTarget<T> ArrayTarget<T>(List<T> array, Func<T, string> idOf) => new(
        id => array.Any(entry => idOf(entry) == id),
        definition => array.Add(definition),
        definition =>
        {
            var index = array.FindIndex(entry => idOf(entry) == idOf(definition));
            array[index] = definition;
        });

    /// <summary>The per-pack <c>merge</c> closure: skips empty/missing collections.</summary>
    private sealed class PackMerger(ContentPack pack, List<PackProblem> problems)
    {
        private readonly HashSet<string> _overrides = [.. pack.Manifest.Overrides ?? []];

        public void Merge<T>(List<T>? definitions, Func<T, string> idOf, MergeTarget<T> target, string label)
        {
            if (definitions is { Count: > 0 }) MergeCollection(pack, definitions, idOf, target, _overrides, problems, label);
        }
    }

    /// <summary>
    /// Layer enabled packs (in resolved order) on top of a GameContent. Used at
    /// play time so mods apply without touching the authored project fields.
    /// </summary>
    public static MergePacksIntoContentResult MergePacksIntoContent(GameContent @base, List<PackInstallation> installs)
    {
        var (packs, problems) = ResolvePackOrder(installs);
        if (packs.Count == 0) return new MergePacksIntoContentResult(@base, problems);

        var crops = new OrderedDictionary<string, CropDefinition>(@base.Crops);
        var items = new List<Item>(@base.Items);
        var recipes = new List<RecipeDefinition>(@base.Recipes);
        var machineTypes = new List<MachineTypeDefinition>(@base.MachineTypes);
        var nodeTypes = new List<NodeTypeDefinition>(@base.NodeTypes);
        var animalSpecies = new List<AnimalSpeciesDefinition>(@base.AnimalSpecies);
        var fishTables = new List<FishTable>(@base.FishTables);
        var weatherTypes = new List<WeatherTypeDefinition>(@base.Weather.Types);
        var npcs = new List<Npc>(@base.Npcs);
        var dialogues = new List<Dialogue>(@base.Dialogues);
        var scenes = new List<Scene>(@base.Scenes);
        var events = new List<GameEvent>(@base.Events);
        var quests = new List<Quest>(@base.Quests);
        var shops = new List<ShopDefinition>(@base.Shops);
        var actions = new List<ActionDef>(@base.Actions);
        var minigames = new List<MinigameDef>(@base.Minigames);

        foreach (var rawPack in packs)
        {
            var pack = NamespacePack(rawPack);
            var merger = new PackMerger(pack, problems);
            var c = pack.Content;

            merger.Merge(c.Crops, d => d.Id, new MergeTarget<CropDefinition>(
                id => crops.ContainsKey(id),
                definition => crops[definition.Id] = definition,
                definition => crops[definition.Id] = definition), "crop");
            merger.Merge(c.Items, d => d.Id, ArrayTarget(items, d => d.Id), "item");
            merger.Merge(c.Recipes, d => d.Id, ArrayTarget(recipes, d => d.Id), "recipe");
            merger.Merge(c.MachineTypes, d => d.Id, ArrayTarget(machineTypes, d => d.Id), "machine type");
            merger.Merge(c.NodeTypes, d => d.Id, ArrayTarget(nodeTypes, d => d.Id), "node type");
            merger.Merge(c.AnimalSpecies, d => d.Id, ArrayTarget(animalSpecies, d => d.Id), "animal species");
            merger.Merge(c.FishTables, d => d.Id, ArrayTarget(fishTables, d => d.Id), "fish table");
            merger.Merge(c.WeatherTypes, d => d.Id, ArrayTarget(weatherTypes, d => d.Id), "weather type");
            merger.Merge(c.Npcs, d => d.Id, ArrayTarget(npcs, d => d.Id), "NPC");
            merger.Merge(c.Dialogues, d => d.Id, ArrayTarget(dialogues, d => d.Id), "dialogue");
            merger.Merge(c.Scenes, d => d.Id, ArrayTarget(scenes, d => d.Id), "scene");
            merger.Merge(c.Events, d => d.Id, ArrayTarget(events, d => d.Id), "event");
            merger.Merge(c.Quests, d => d.Id, ArrayTarget(quests, d => d.Id), "quest");
            merger.Merge(c.Shops, d => d.Id, ArrayTarget(shops, d => d.Id), "shop");
            merger.Merge(c.Actions, d => d.Id, ArrayTarget(actions, d => d.Id), "action");
            merger.Merge(c.Minigames, d => d.Id, ArrayTarget(minigames, d => d.Id), "minigame");
        }

        var content = @base with
        {
            Crops = crops,
            Items = items,
            Recipes = recipes,
            MachineTypes = machineTypes,
            NodeTypes = nodeTypes,
            AnimalSpecies = animalSpecies,
            FishTables = fishTables,
            Weather = @base.Weather with { Types = weatherTypes },
            Npcs = npcs,
            Dialogues = dialogues,
            Scenes = scenes,
            Events = events,
            Quests = quests,
            Shops = shops,
            Actions = actions,
            Minigames = minigames,
        };
        return new MergePacksIntoContentResult(content, problems);
    }

    /// <summary>A pack crop (CropDefinition) stored as a project custom crop (TS structural typing).</summary>
    private static CustomCropDefinition ToCustomCrop(CropDefinition crop) =>
        JsonSerializer.SerializeToNode(crop, JsonDefaults.Options).Deserialize<CustomCropDefinition>(JsonDefaults.Options)!;

    /// <summary>
    /// Materialize a pack's content into a project (the editor's "import into
    /// project" and the starter-game seed). Same namespacing and override rules
    /// as play-time merging; returns problems for anything skipped.
    /// </summary>
    public static ApplyPackToProjectResult ApplyPackToProject(GameProject project, ContentPack rawPack)
    {
        var problems = new List<PackProblem>();
        var pack = NamespacePack(rawPack);
        var merger = new PackMerger(pack, problems);

        var customCrops = new List<CustomCropDefinition>(project.CustomCrops ?? []);
        var items = new List<Item>(project.Items);
        var recipes = new List<RecipeDefinition>(project.Recipes ?? []);
        var machineTypes = new List<MachineTypeDefinition>(project.MachineTypes ?? []);
        var nodeTypes = new List<NodeTypeDefinition>(project.NodeTypes ?? []);
        var animalSpecies = new List<AnimalSpeciesDefinition>(project.AnimalSpecies ?? []);
        var fishTables = new List<FishTable>(project.FishTables ?? []);
        var weatherTypes = new List<WeatherTypeDefinition>(project.Weather?.Types ?? []);
        var npcs = new List<Npc>(project.Npcs);
        var dialogues = new List<Dialogue>(project.Dialogues);
        var scenes = new List<Scene>(project.Scenes);
        var events = new List<GameEvent>(project.Events);
        var quests = new List<Quest>(project.Quests);
        var shops = new List<ShopDefinition>(project.Shops ?? []);
        var actions = new List<ActionDef>(project.Actions ?? []);
        var minigames = new List<MinigameDef>(project.Minigames ?? []);

        var c = pack.Content;
        merger.Merge(c.Crops, d => d.Id, new MergeTarget<CropDefinition>(
            id => customCrops.Any(entry => entry.Id == id),
            definition => customCrops.Add(ToCustomCrop(definition)),
            definition =>
            {
                var index = customCrops.FindIndex(entry => entry.Id == definition.Id);
                customCrops[index] = ToCustomCrop(definition);
            }), "crop");
        merger.Merge(c.Items, d => d.Id, ArrayTarget(items, d => d.Id), "item");
        merger.Merge(c.Recipes, d => d.Id, ArrayTarget(recipes, d => d.Id), "recipe");
        merger.Merge(c.MachineTypes, d => d.Id, ArrayTarget(machineTypes, d => d.Id), "machine type");
        merger.Merge(c.NodeTypes, d => d.Id, ArrayTarget(nodeTypes, d => d.Id), "node type");
        merger.Merge(c.AnimalSpecies, d => d.Id, ArrayTarget(animalSpecies, d => d.Id), "animal species");
        merger.Merge(c.FishTables, d => d.Id, ArrayTarget(fishTables, d => d.Id), "fish table");
        merger.Merge(c.WeatherTypes, d => d.Id, ArrayTarget(weatherTypes, d => d.Id), "weather type");
        merger.Merge(c.Npcs, d => d.Id, ArrayTarget(npcs, d => d.Id), "NPC");
        merger.Merge(c.Dialogues, d => d.Id, ArrayTarget(dialogues, d => d.Id), "dialogue");
        merger.Merge(c.Scenes, d => d.Id, ArrayTarget(scenes, d => d.Id), "scene");
        merger.Merge(c.Events, d => d.Id, ArrayTarget(events, d => d.Id), "event");
        merger.Merge(c.Quests, d => d.Id, ArrayTarget(quests, d => d.Id), "quest");
        merger.Merge(c.Shops, d => d.Id, ArrayTarget(shops, d => d.Id), "shop");
        merger.Merge(c.Actions, d => d.Id, ArrayTarget(actions, d => d.Id), "action");
        merger.Merge(c.Minigames, d => d.Id, ArrayTarget(minigames, d => d.Id), "minigame");

        var next = project with
        {
            CustomCrops = customCrops,
            Items = items,
            Recipes = recipes,
            MachineTypes = machineTypes,
            NodeTypes = nodeTypes,
            AnimalSpecies = animalSpecies,
            FishTables = fishTables,
            Weather = (project.Weather ?? new WeatherConfig()) with { Types = weatherTypes },
            Npcs = npcs,
            Dialogues = dialogues,
            Scenes = scenes,
            Events = events,
            Quests = quests,
            Shops = shops,
            Actions = actions,
            Minigames = minigames,
        };

        var start = c.PlayerStart;
        if (start is not null)
        {
            // `new Map(...)`: a later duplicate item id wins.
            var itemById = new Dictionary<string, Item>();
            foreach (var item in next.Items) itemById[item.Id] = item;
            var inventory = new List<InventorySlot>(next.Player.Inventory);
            foreach (var slot in start.Inventory ?? [])
            {
                if (!itemById.TryGetValue(slot.ItemId, out var item))
                {
                    problems.Add(new PackProblem(
                        pack.Manifest.Id,
                        "error",
                        $"playerStart references unknown item '{slot.ItemId}'"));
                    continue;
                }
                inventory.Add(new InventorySlot { Item = item, Quantity = slot.Quantity });
            }
            next = next with
            {
                Player = next.Player with
                {
                    Inventory = inventory,
                    Money = start.Money ?? next.Player.Money,
                    SceneId = start.SceneId ?? next.Player.SceneId,
                    X = start.X ?? next.Player.X,
                    Y = start.Y ?? next.Player.Y,
                },
            };
            if (!string.IsNullOrEmpty(start.SceneId)) next = next with { StartSceneId = start.SceneId };
        }

        return new ApplyPackToProjectResult(next, problems);
    }

    /// <summary>All pack problems for a project (Problems panel): order + dry-run merge.</summary>
    public static List<PackProblem> CollectPackProblems(GameProject project, GameContent content) =>
        MergePacksIntoContent(content, project.ContentPacks ?? []).Problems;

    /// <summary>The pack stamp written into saves.</summary>
    public static List<SavePackRef> StampPacks(List<PackInstallation> installs) =>
        (installs ?? [])
            .Where(install => install.Enabled)
            .Select(install => new SavePackRef { Id = install.Pack.Manifest.Id, Version = install.Pack.Manifest.Version })
            .ToList();

    /// <summary>
    /// Quarantine inventory items whose owning pack is missing or disabled, and
    /// restore quarantined items whose pack came back. Items are never dropped.
    /// </summary>
    public static GameState ReconcilePackItems(GameState state, IReadOnlySet<string> enabledPackIds)
    {
        bool IsAvailable(string itemId)
        {
            var owner = PackIdOf(itemId);
            return owner is null || enabledPackIds.Contains(owner);
        }

        var quarantined = state.QuarantinedItems ?? [];
        var keep = state.Player.Inventory.Where(slot => IsAvailable(slot.Item.Id)).ToList();
        var toQuarantine = state.Player.Inventory.Where(slot => !IsAvailable(slot.Item.Id)).ToList();
        var toRestore = quarantined.Where(slot => IsAvailable(slot.Item.Id)).ToList();
        var stillQuarantined = quarantined.Where(slot => !IsAvailable(slot.Item.Id)).ToList();

        if (toQuarantine.Count == 0 && toRestore.Count == 0) return state;

        return state with
        {
            Player = state.Player with { Inventory = [.. keep, .. toRestore] },
            QuarantinedItems = [.. stillQuarantined, .. toQuarantine],
        };
    }

    public sealed record ResolvePackOrderResult(List<ContentPack> Packs, List<PackProblem> Problems);

    public sealed record MergePacksIntoContentResult(GameContent Content, List<PackProblem> Problems);

    public sealed record ApplyPackToProjectResult(GameProject Project, List<PackProblem> Problems);
}
