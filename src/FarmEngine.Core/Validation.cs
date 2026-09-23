using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Project linting (M3 "Problems" panel) — port of validation.ts. Pure content
/// checks — the same validations run on import. Every check targets a
/// dangling-reference or unreachable-content class of bug.
/// </summary>
public static class Validation
{
    public static List<Problem> ValidateProjectContent(GameProject project)
    {
        var problems = new List<Problem>();
        var scenes = project.Scenes ?? [];
        var sceneIds = new HashSet<string>(scenes.Select(scene => scene.Id));
        var itemIds = new HashSet<string>((project.Items ?? []).Select(item => item.Id));
        var npcIds = new HashSet<string>((project.Npcs ?? []).Select(npc => npc.Id));
        var questIds = new HashSet<string>((project.Quests ?? []).Select(quest => quest.Id));
        var shopIds = new HashSet<string>((project.Shops ?? []).Select(shop => shop.Id));
        var crops = Crops.MergeCropDefinitions(project.CustomCrops);
        var nodeTypeIds = new HashSet<string>(
            ContentBuiltin.DefaultNodeTypes.Select(def => def.Id)
                .Concat((project.NodeTypes ?? []).Select(def => def.Id)));
        var dialogueIds = new HashSet<string>();
        foreach (var dialogue in project.Dialogues ?? []) dialogueIds.Add(dialogue.Id);
        foreach (var npc in project.Npcs ?? [])
        {
            foreach (var dialogue in npc.Dialogue ?? []) dialogueIds.Add(dialogue.Id);
        }

        // Start scene
        if (!sceneIds.Contains(project.StartSceneId))
        {
            problems.Add(new Problem("error", "scenes", $"Start scene \"{project.StartSceneId}\" does not exist", project.StartSceneId));
        }

        // Transitions
        foreach (var scene in scenes)
        {
            foreach (var transition in scene.Transitions ?? [])
            {
                if (!sceneIds.Contains(transition.ToSceneId))
                {
                    problems.Add(new Problem(
                        "error", "transitions",
                        $"Transition in \"{scene.Name}\" at ({Js.Num(transition.FromX)},{Js.Num(transition.FromY)}) leads to missing scene \"{transition.ToSceneId}\"",
                        scene.Id));
                }
                else
                {
                    var target = scenes.First(s => s.Id == transition.ToSceneId);
                    if (transition.ToX < 0 || transition.ToX >= target.Width || transition.ToY < 0 || transition.ToY >= target.Height)
                    {
                        problems.Add(new Problem(
                            "error", "transitions",
                            $"Transition in \"{scene.Name}\" lands out of bounds at ({Js.Num(transition.ToX)},{Js.Num(transition.ToY)}) in \"{target.Name}\"",
                            scene.Id));
                    }
                }
            }
        }

        // Unreachable scenes (no transition in, not the start scene)
        var reachable = new HashSet<string> { project.StartSceneId };
        foreach (var scene in scenes)
        {
            foreach (var transition in scene.Transitions ?? []) reachable.Add(transition.ToSceneId);
        }
        foreach (var scene in scenes)
        {
            // transient mine floors
            if (scene.Extra is not null && scene.Extra.TryGetValue("generated", out var generated) && Js.Truthy(generated)) continue;
            if (!reachable.Contains(scene.Id))
            {
                problems.Add(new Problem("warning", "scenes", $"Scene \"{scene.Name}\" is unreachable (no transition leads to it)", scene.Id));
            }
        }

        // Dialogue references
        void CheckDialogue(Dialogue dialogue)
        {
            foreach (var option in dialogue.Options ?? [])
            {
                if (!string.IsNullOrEmpty(option.NextDialogueId) && !dialogueIds.Contains(option.NextDialogueId))
                {
                    problems.Add(new Problem("error", "dialogue", $"Dialogue \"{dialogue.Id}\" links to missing dialogue \"{option.NextDialogueId}\"", dialogue.NpcId));
                }
                if (!string.IsNullOrEmpty(option.GiveItem) && !itemIds.Contains(option.GiveItem))
                {
                    problems.Add(new Problem("error", "dialogue", $"Dialogue \"{dialogue.Id}\" gives missing item \"{option.GiveItem}\"", dialogue.NpcId));
                }
                if (!string.IsNullOrEmpty(option.OpenShopId) && !shopIds.Contains(option.OpenShopId))
                {
                    problems.Add(new Problem("error", "dialogue", $"Dialogue \"{dialogue.Id}\" opens missing shop \"{option.OpenShopId}\"", dialogue.NpcId));
                }
                if (!string.IsNullOrEmpty(option.OfferQuestId) && !questIds.Contains(option.OfferQuestId))
                {
                    problems.Add(new Problem("error", "dialogue", $"Dialogue \"{dialogue.Id}\" offers missing quest \"{option.OfferQuestId}\"", dialogue.NpcId));
                }
            }
        }
        foreach (var dialogue in project.Dialogues ?? []) CheckDialogue(dialogue);
        foreach (var npc in project.Npcs ?? [])
        {
            foreach (var dialogue in npc.Dialogue ?? []) CheckDialogue(dialogue);
            if (!sceneIds.Contains(npc.SceneId))
            {
                problems.Add(new Problem("error", "npcs", $"NPC \"{npc.Name}\" is placed in missing scene \"{npc.SceneId}\"", npc.Id));
            }
            foreach (var entry in npc.Schedule ?? [])
            {
                if (!sceneIds.Contains(entry.SceneId))
                {
                    problems.Add(new Problem("error", "npcs", $"NPC \"{npc.Name}\" schedule targets missing scene \"{entry.SceneId}\"", npc.Id));
                }
            }
        }

        // Quests
        foreach (var quest in project.Quests ?? [])
        {
            foreach (var prereq in quest.Prerequisites ?? [])
            {
                if (!questIds.Contains(prereq))
                {
                    problems.Add(new Problem("error", "quests", $"Quest \"{quest.Name}\" requires missing quest \"{prereq}\"", quest.Id));
                }
            }
            foreach (var objective in quest.Objectives ?? [])
            {
                if (!string.IsNullOrEmpty(objective.TargetItemId) && !itemIds.Contains(objective.TargetItemId))
                {
                    problems.Add(new Problem("error", "quests", $"Quest \"{quest.Name}\" objective targets missing item \"{objective.TargetItemId}\"", quest.Id));
                }
                if (!string.IsNullOrEmpty(objective.TargetNpcId) && !npcIds.Contains(objective.TargetNpcId))
                {
                    problems.Add(new Problem("error", "quests", $"Quest \"{quest.Name}\" objective targets missing NPC \"{objective.TargetNpcId}\"", quest.Id));
                }
                if (!string.IsNullOrEmpty(objective.TargetSceneId) && !sceneIds.Contains(objective.TargetSceneId))
                {
                    problems.Add(new Problem("error", "quests", $"Quest \"{quest.Name}\" objective targets missing scene \"{objective.TargetSceneId}\"", quest.Id));
                }
                if (!string.IsNullOrEmpty(objective.TargetCropType) && !crops.ContainsKey(objective.TargetCropType))
                {
                    problems.Add(new Problem("error", "quests", $"Quest \"{quest.Name}\" objective targets missing crop \"{objective.TargetCropType}\"", quest.Id));
                }
            }
            foreach (var reward in quest.Rewards?.Items ?? [])
            {
                if (!itemIds.Contains(reward.ItemId))
                {
                    problems.Add(new Problem("error", "quests", $"Quest \"{quest.Name}\" rewards missing item \"{reward.ItemId}\"", quest.Id));
                }
            }
        }

        // Events
        foreach (var @event in project.Events ?? [])
        {
            if (!string.IsNullOrEmpty(@event.SceneId) && !sceneIds.Contains(@event.SceneId))
            {
                problems.Add(new Problem("error", "events", $"Event \"{@event.Name}\" belongs to missing scene \"{@event.SceneId}\"", @event.Id));
            }
            foreach (var condition in @event.Conditions ?? [])
            {
                if (condition is HasItemCondition hasItem && !itemIds.Contains(hasItem.ItemId))
                {
                    problems.Add(new Problem("error", "events", $"Event \"{@event.Name}\" checks missing item \"{hasItem.ItemId}\"", @event.Id));
                }
                if (condition is QuestStatusCondition questStatus && !questIds.Contains(questStatus.QuestId))
                {
                    problems.Add(new Problem("error", "events", $"Event \"{@event.Name}\" checks missing quest \"{questStatus.QuestId}\"", @event.Id));
                }
            }
            foreach (var outcome in @event.Outcomes ?? [])
            {
                if ((outcome.Type == "giveItem" || outcome.Type == "takeItem") && !string.IsNullOrEmpty(outcome.ItemId) && !itemIds.Contains(outcome.ItemId))
                {
                    problems.Add(new Problem("error", "events", $"Event \"{@event.Name}\" references missing item \"{outcome.ItemId}\"", @event.Id));
                }
                if ((outcome.Type == "startQuest" || outcome.Type == "completeQuest") && !string.IsNullOrEmpty(outcome.QuestId) && !questIds.Contains(outcome.QuestId))
                {
                    problems.Add(new Problem("error", "events", $"Event \"{@event.Name}\" references missing quest \"{outcome.QuestId}\"", @event.Id));
                }
                if ((outcome.Type == "spawnNPC" || outcome.Type == "removeNPC" || outcome.Type == "startDialogue") && !string.IsNullOrEmpty(outcome.NpcId) && !npcIds.Contains(outcome.NpcId))
                {
                    problems.Add(new Problem("error", "events", $"Event \"{@event.Name}\" references missing NPC \"{outcome.NpcId}\"", @event.Id));
                }
                if (outcome.Type == "warpPlayer" && !string.IsNullOrEmpty(outcome.SceneId) && !sceneIds.Contains(outcome.SceneId))
                {
                    problems.Add(new Problem("error", "events", $"Event \"{@event.Name}\" warps to missing scene \"{outcome.SceneId}\"", @event.Id));
                }
            }
        }

        // Shops
        foreach (var shop in project.Shops ?? [])
        {
            foreach (var entry in shop.Stock ?? [])
            {
                if (!itemIds.Contains(entry.ItemId))
                {
                    problems.Add(new Problem("error", "shops", $"Shop \"{shop.Name}\" stocks missing item \"{entry.ItemId}\"", shop.Id));
                }
            }
        }

        // Node types + placed nodes
        foreach (var nodeType in project.NodeTypes ?? [])
        {
            foreach (var drop in nodeType.Drops ?? [])
            {
                if (!itemIds.Contains(drop.ItemId))
                {
                    problems.Add(new Problem("error", "nodes", $"Node type \"{nodeType.Name}\" drops missing item \"{drop.ItemId}\"", nodeType.Id));
                }
            }
        }
        foreach (var scene in scenes)
        {
            foreach (var row in scene.Tiles ?? [])
            {
                foreach (var tile in row)
                {
                    if (tile.Node is not null && !nodeTypeIds.Contains(tile.Node.TypeId))
                    {
                        problems.Add(new Problem("error", "nodes", $"Scene \"{scene.Name}\" has a placed node of missing type \"{tile.Node.TypeId}\" at ({Js.Num(tile.X)},{Js.Num(tile.Y)})", scene.Id));
                    }
                    if (tile.Crop is not null && !crops.ContainsKey(tile.Crop.Type))
                    {
                        problems.Add(new Problem("warning", "crops", $"Scene \"{scene.Name}\" has a planted crop of missing type \"{tile.Crop.Type}\" at ({Js.Num(tile.X)},{Js.Num(tile.Y)})", scene.Id));
                    }
                    if (tile.Crop is not null && crops.TryGetValue(tile.Crop.Type, out var cropDef) && !Crops.CanGrowInSeason(cropDef, project.CurrentSeason))
                    {
                        problems.Add(new Problem("warning", "crops", $"Scene \"{scene.Name}\": {cropDef.Name} at ({Js.Num(tile.X)},{Js.Num(tile.Y)}) cannot grow in the starting season ({project.CurrentSeason})", scene.Id));
                    }
                }
            }
        }

        // Seeds referencing missing crops
        foreach (var item in project.Items ?? [])
        {
            if (item.Type == "seed" && !string.IsNullOrEmpty(item.CropType) && !crops.ContainsKey(item.CropType))
            {
                problems.Add(new Problem("error", "items", $"Seed \"{item.Name}\" references missing crop \"{item.CropType}\"", item.Id));
            }
        }

        // Content packs (M5): load-order errors, compatibility warnings and
        // undeclared-override conflicts from a dry-run merge.
        if (project.ContentPacks is { Count: > 0 })
        {
            var packProblems = Packs.MergePacksIntoContent(EngineState.CreateBaseContentFromProject(project), project.ContentPacks).Problems;
            foreach (var packProblem in packProblems)
            {
                problems.Add(new Problem(packProblem.Severity, "packs", packProblem.Message, packProblem.PackId));
            }
        }

        return problems;
    }

    /// <summary>TS <c>ProblemSeverity</c>.</summary>
    public static class ProblemSeverities
    {
        public const string Error = "error";
        public const string Warning = "warning";
    }

    /// <summary>TS <c>Problem</c>. <c>Severity</c> is one of <see cref="ProblemSeverities"/>.</summary>
    /// <param name="Subject">Where to look (scene/npc/quest/... id) for editor deep-linking.</param>
    public sealed record Problem(string Severity, string Category, string Message, string? Subject = null);
}
