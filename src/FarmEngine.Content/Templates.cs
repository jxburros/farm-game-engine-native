using FarmEngine.Schemas;

namespace FarmEngine.Content;

/// <summary>A "New Project" template entry (TS <c>TEMPLATE_INFO</c> element).</summary>
/// <param name="Id">One of <see cref="ProjectTemplates"/>.</param>
public sealed record TemplateInfo(string Id, string Name, string Description);

/// <summary>TS <c>ProjectTemplate = 'starter' | 'blank' | 'cozy' | 'quest'</c>.</summary>
public static class ProjectTemplates
{
    public const string Starter = "starter";
    public const string Blank = "blank";
    public const string Cozy = "cozy";
    public const string Quest = "quest";

    public static readonly IReadOnlyList<string> All = [Starter, Blank, Cozy, Quest];
}

/// <summary>
/// Sample game templates (M8) — port of <c>src/lib/templates.ts</c>: small
/// complete games that double as starting points in "New Project". Each
/// builds on the starter farm so they stay in sync with content-default.
/// Also carries the project-creation and <c>?sample=</c> boot logic from
/// <c>src/lib/projects.ts</c> / <c>src/App.tsx</c>, minus browser storage.
/// </summary>
public static class Templates
{
    public static readonly IReadOnlyList<TemplateInfo> TemplateInfo =
    [
        new(ProjectTemplates.Starter, "Starter Farm", "The full farming loop: crops, shop, quests, crafting."),
        new(ProjectTemplates.Cozy, "Cozy Garden", "Pure-farm relaxation — no energy, no collapse, slow days."),
        new(ProjectTemplates.Quest, "Quest RPG", "Story-driven: a quest chain, gated dialogue and an elder NPC."),
        new(ProjectTemplates.Blank, "Blank", "An empty scene and the default catalog. Build from scratch."),
    ];

    /// <summary>
    /// Browser-playable samples (examples/README.md, App.tsx <c>?sample=</c>):
    /// each boots a fresh, isolated project straight into Play Mode.
    /// </summary>
    public static readonly IReadOnlyList<string> SampleIds = [ProjectTemplates.Starter, ProjectTemplates.Cozy, ProjectTemplates.Quest];

    /// <summary>Pure-farm cozy template: heavy systems off, generous clock.</summary>
    public static GameProject CreateCozyFarmProject(double? now = null)
    {
        var project = DefaultContent.CreateInitialProject(now);
        return project with
        {
            Name = "Cozy Garden",
            Settings = project.Settings with
            {
                EnergyEnabled = false,
                CollapseMoneyPenalty = 0,
                Time = project.Settings.Time with { MinutesPerRealSecond = 0.5 },
            },
            // A cozy game starts with more seeds and a watering head start.
            Player = project.Player with
            {
                Money = 250,
                Inventory = project.Player.Inventory
                    .Select(slot => slot.Item.Type == "seed" ? slot with { Quantity = slot.Quantity + 10 } : slot)
                    .ToList(),
            },
        };
    }

    /// <summary>Quest-driven RPG template: an elder NPC and a three-step story chain.</summary>
    public static GameProject CreateQuestRpgProject(double? now = null)
    {
        var project = DefaultContent.CreateInitialProject(now);

        var elderDialogue = new Dialogue
        {
            Id = "dialogue-elder-intro",
            NpcId = "npc-elder",
            Text = "Our village once glowed with festival lanterns. Bring me wood and stone, and we will rebuild the square.",
            Options =
            [
                new DialogueOption { Text = "I will help.", OfferQuestId = "quest-rebuild-square" },
                new DialogueOption { Text = "Maybe later." },
            ],
        };
        var elder = new Npc
        {
            Id = "npc-elder",
            Name = "Elder Rowan",
            X = 2,
            Y = 3,
            SceneId = "scene-farm",
            Dialogue = [elderDialogue],
            CanMove = false,
            MovePattern = "stationary",
            Appearance = "farmer",
        };

        List<Quest> quests =
        [
            new Quest
            {
                Id = "quest-rebuild-square",
                Name = "Rebuild the Square",
                Description = "Gather 5 wood and 3 stone for Elder Rowan.",
                Giver = "npc-elder",
                Status = "not-started",
                Objectives =
                [
                    new QuestObjective { Id = "obj-wood", Type = "collect", Description = "Collect 5 wood", TargetItemId = "material-wood", Extra = TargetQuantity(5), Completed = false, Progress = 0 },
                    new QuestObjective { Id = "obj-stone", Type = "collect", Description = "Collect 3 stone", TargetItemId = "material-stone", Extra = TargetQuantity(3), Completed = false, Progress = 0 },
                ],
                Rewards = new QuestRewards { Money = 200 },
                AutoStart = false,
                Repeatable = false,
            },
            new Quest
            {
                Id = "quest-festival-feast",
                Name = "Festival Feast",
                Description = "Grow the harvest for the festival: 5 wheat.",
                Giver = "npc-elder",
                Status = "not-started",
                Objectives =
                [
                    new QuestObjective { Id = "obj-feast-wheat", Type = "harvest", Description = "Harvest 5 wheat", TargetCropType = "wheat", TargetCropQuantity = 5, Completed = false, Progress = 0 },
                ],
                Rewards = new QuestRewards { Money = 300, Items = [new QuestRewardItem { ItemId = "gift-flower", Quantity = 3 }] },
                Prerequisites = ["quest-rebuild-square"],
                AutoStart = true,
                Repeatable = false,
            },
        ];

        return project with
        {
            Name = "Quest RPG",
            Npcs = [.. project.Npcs, elder],
            Dialogues = [.. project.Dialogues, elderDialogue],
            Quests = [.. project.Quests, .. quests],
        };
    }

    /// <summary>
    /// The TS template writes <c>targetQuantity</c> — not a declared
    /// <c>QuestObjective</c> field (the schema has <c>targetItemQuantity</c>), so it
    /// rides the passthrough bag exactly as in the TS project JSON.
    /// </summary>
    private static Dictionary<string, System.Text.Json.JsonElement> TargetQuantity(double value) =>
        new() { ["targetQuantity"] = FarmEngine.Json.Js.Value(value) };

    /// <summary>TS <c>createProjectFromTemplate</c>: cozy/quest only; starter/blank return null (handled by their factories).</summary>
    public static GameProject? CreateProjectFromTemplate(string template, double? now = null) => template switch
    {
        ProjectTemplates.Cozy => CreateCozyFarmProject(now),
        ProjectTemplates.Quest => CreateQuestRpgProject(now),
        _ => null, // starter/blank handled by their existing factories
    };

    /// <summary>
    /// Any template's fresh project (projects.ts <c>createProject</c> without the
    /// storage side): template factory, else starter → initial project, else blank.
    /// </summary>
    public static GameProject CreateProjectForTemplate(string template, double? now = null) =>
        CreateProjectFromTemplate(template, now)
        ?? (template == ProjectTemplates.Starter ? DefaultContent.CreateInitialProject(now) : DefaultContent.CreateBlankProject(now));

    /// <summary>
    /// projects.ts <c>createProject(template, name)</c> minus localStorage: a fresh
    /// project for the template with a new id (<c>proj-{Date.now() base 36}</c>)
    /// and the given name. Persisting it is the host's job.
    /// </summary>
    public static GameProject CreateNewProject(string template, string name, string? id = null, double? now = null)
    {
        var time = now ?? DefaultContent.Now();
        var project = CreateProjectForTemplate(template, time);
        return project with { Id = id ?? NewProjectId(time), Name = name };
    }

    /// <summary>TS <c>`proj-${Date.now().toString(36)}`</c>.</summary>
    public static string NewProjectId(double? now = null) => $"proj-{ToBase36((long)(now ?? DefaultContent.Now()))}";

    /// <summary>
    /// App.tsx <c>?sample=</c> boot: 'starter' | 'cozy' | 'quest' build that sample;
    /// anything else (including null) is the starter farm.
    /// </summary>
    public static GameProject CreateSampleProject(string? sampleId, double? now = null)
    {
        if (sampleId is null || sampleId == ProjectTemplates.Starter || !SampleIds.Contains(sampleId))
            return DefaultContent.CreateInitialProject(now);
        return CreateProjectFromTemplate(sampleId, now) ?? DefaultContent.CreateInitialProject(now);
    }

    private static string ToBase36(long value)
    {
        if (value == 0) return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var negative = value < 0;
        var n = negative ? -value : value;
        var chars = new Stack<char>();
        while (n > 0)
        {
            chars.Push(digits[(int)(n % 36)]);
            n /= 36;
        }
        return (negative ? "-" : "") + new string([.. chars]);
    }
}
