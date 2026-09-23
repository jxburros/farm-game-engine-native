using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Quest progression over GameState (port of quests/quests.ts): progress
/// clamped to target, auto-completion when all objectives finish; rewards that
/// don't fit the inventory raise an error message rather than vanishing silently.
/// </summary>
public static class Quests
{
    private static Quest? GetQuestDefinition(EngineContext ctx, string questId) =>
        ctx.Content.Quests.FirstOrDefault(quest => quest.Id == questId);

    private static double ObjectiveTarget(QuestObjective objective) =>
        // ?? not ||: an authored target of 0 means "already satisfied", not 1.
        objective.TargetItemQuantity ?? objective.TargetCropQuantity ?? 1;

    /// <summary>Complete a quest: move it to completed, grant rewards.</summary>
    private static EngineStep CompleteQuest(EngineContext ctx, GameState state, string questId)
    {
        var quest = GetQuestDefinition(ctx, questId);
        if (quest is null) return EngineStep.Of(state);

        var player = state.Player with
        {
            ActiveQuests = state.Player.ActiveQuests.Where(id => id != questId).ToList(),
            CompletedQuests = [.. state.Player.CompletedQuests, questId],
        };

        if (quest.Rewards.Money is { } money && money != 0 && !double.IsNaN(money))
        {
            player = player with { Money = player.Money + money };
        }

        var effects = new List<Effect>();
        if (quest.Rewards.Items is not null)
        {
            var inventory = player.Inventory;
            foreach (var reward in quest.Rewards.Items)
            {
                var item = ctx.Content.Items.FirstOrDefault(i => i.Id == reward.ItemId);
                if (item is null) continue;
                var result = Inventory.AddItem(inventory, item, reward.Quantity, player.MaxInventorySize);
                inventory = result.Inventory;
                if (!result.Added)
                {
                    // A dropped reward is a player-visible loss — say so instead of
                    // silently discarding it.
                    effects.Add(Effect.Message(MessageLevels.Error, $"Inventory full — quest reward lost: {Js.Num(reward.Quantity)}× {item.Name}"));
                }
            }
            player = player with { Inventory = inventory };
        }

        // TS: { ...state.quests[questId], status: 'completed' }. A missing entry
        // spreads to nothing; here it becomes an entry with empty objectives.
        var completed = state.Quests.TryGetValue(questId, out var existing)
            ? existing with { Status = "completed" }
            : new QuestProgress { Status = "completed" };
        var quests = new OrderedDictionary<string, QuestProgress>(state.Quests) { [questId] = completed };

        return new EngineStep(
            state with { Player = player, Quests = quests },
            [new QuestCompletedEffect(questId), .. effects]);
    }

    /// <summary>
    /// Progress matching objectives of active quests.
    /// kind: 'harvest' matches targetCropType, 'collect' targetItemId,
    /// 'talk' targetNPCId, 'visit' targetSceneId, 'craft' targetItemId, 'gift' targetNPCId.
    /// </summary>
    /// <param name="kind">'collect' | 'harvest' | 'talk' | 'visit' | 'craft' | 'gift'</param>
    public static EngineStep ProgressQuests(EngineContext ctx, GameState state, string kind, string targetId, double amount)
    {
        var currentState = state;
        var effects = new List<Effect>();

        foreach (var questId in state.Player.ActiveQuests.ToList())
        {
            var quest = GetQuestDefinition(ctx, questId);
            var progress = currentState.Quests.TryGetValue(questId, out var p) ? p : null;
            if (quest is null || progress is null || progress.Status != "active") continue;

            var changed = false;
            var objectives = new OrderedDictionary<string, QuestObjectiveProgress>(progress.Objectives);

            foreach (var objective in quest.Objectives)
            {
                var objectiveState = objectives.TryGetValue(objective.Id, out var os) ? os : new QuestObjectiveProgress { Progress = 0, Completed = false };
                if (objective.Type != kind || objectiveState.Completed) continue;

                var matches =
                    (kind == "harvest" && objective.TargetCropType == targetId) ||
                    (kind == "collect" && objective.TargetItemId == targetId) ||
                    (kind == "talk" && objective.TargetNpcId == targetId) ||
                    (kind == "visit" && objective.TargetSceneId == targetId) ||
                    (kind == "craft" && objective.TargetItemId == targetId) ||
                    (kind == "gift" && objective.TargetNpcId == targetId);
                if (!matches) continue;

                var target = ObjectiveTarget(objective);
                var newProgress = Math.Min(objectiveState.Progress + amount, target);
                objectives[objective.Id] = new QuestObjectiveProgress { Progress = newProgress, Completed = newProgress >= target };
                changed = true;
            }

            if (!changed) continue;

            currentState = currentState with
            {
                Quests = new OrderedDictionary<string, QuestProgress>(currentState.Quests) { [questId] = progress with { Objectives = objectives } },
            };

            var allComplete = quest.Objectives.All(objective => objectives.TryGetValue(objective.Id, out var o) && o.Completed);
            if (allComplete)
            {
                var completion = CompleteQuest(ctx, currentState, questId);
                currentState = completion.State;
                effects.AddRange(completion.Effects);
            }
        }

        return new EngineStep(currentState, effects);
    }

    /// <summary>Availability window check (M3): seasons + absolute-day range.</summary>
    public static bool IsQuestAvailable(Quest quest, GameState state)
    {
        if (quest.AvailableSeasons is { Count: > 0 } seasons && !seasons.Contains(state.Clock.Season)) return false;
        if (quest.AvailableFromDay is { } from && state.Clock.Day < from) return false;
        if (quest.AvailableToDay is { } to && state.Clock.Day > to) return false;
        return true;
    }

    /// <summary>Start a quest by id (dialogue offers, event outcomes).</summary>
    public static EngineStep StartQuestById(EngineContext ctx, GameState state, string questId)
    {
        var quest = GetQuestDefinition(ctx, questId);
        if (quest is null) return EngineStep.Of(state);
        if (state.Player.ActiveQuests.Contains(questId) || state.Player.CompletedQuests.Contains(questId))
        {
            return EngineStep.Of(state);
        }
        var prerequisitesMet = (quest.Prerequisites ?? []).All(id => state.Player.CompletedQuests.Contains(id));
        if (!prerequisitesMet || !IsQuestAvailable(quest, state))
        {
            return EngineStep.Of(state);
        }
        return EngineStep.Of(
            state with
            {
                Player = state.Player with { ActiveQuests = [.. state.Player.ActiveQuests, questId] },
                Quests = new OrderedDictionary<string, QuestProgress>(state.Quests) { [questId] = Activated(state.Quests, questId) },
            },
            Effect.Message(MessageLevels.Info, $"New quest: {quest.Name}"));
    }

    /// <summary>TS <c>{ ...(quests[id] ?? { objectives: {} }), status: 'active' }</c>.</summary>
    private static QuestProgress Activated(OrderedDictionary<string, QuestProgress> quests, string questId) =>
        quests.TryGetValue(questId, out var existing)
            ? existing with { Status = "active" }
            : new QuestProgress { Objectives = [], Status = "active" };

    /// <summary>Force-complete a quest by id (event outcome).</summary>
    public static EngineStep CompleteQuestById(EngineContext ctx, GameState state, string questId)
    {
        var quest = GetQuestDefinition(ctx, questId);
        if (quest is null) return EngineStep.Of(state);
        if (!state.Player.ActiveQuests.Contains(questId)) return EngineStep.Of(state);
        return CompleteQuest(ctx, state, questId);
    }

    /// <summary>Activate autoStart quests whose prerequisites are complete.</summary>
    public static GameState AutoStartQuests(EngineContext ctx, GameState state)
    {
        var player = state.Player;
        var quests = state.Quests;

        foreach (var quest in ctx.Content.Quests)
        {
            if (quest.AutoStart != true) continue;
            if (player.ActiveQuests.Contains(quest.Id) || player.CompletedQuests.Contains(quest.Id)) continue;
            var completedQuests = player.CompletedQuests;
            var prerequisitesMet = (quest.Prerequisites ?? []).All(id => completedQuests.Contains(id));
            if (!prerequisitesMet) continue;
            if (!IsQuestAvailable(quest, state with { Player = player })) continue;

            player = player with { ActiveQuests = [.. player.ActiveQuests, quest.Id] };
            quests = new OrderedDictionary<string, QuestProgress>(quests) { [quest.Id] = Activated(quests, quest.Id) };
        }

        if (ReferenceEquals(player, state.Player) && ReferenceEquals(quests, state.Quests)) return state;
        return state with { Player = player, Quests = quests };
    }
}
