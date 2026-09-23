using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Player skills (M4g): XP per action category; levels unlock recipes and
/// grant small passive modifiers. Toggleable per project. Port of skills.ts.
/// </summary>
public static class Skills
{
    public static double SkillLevelForXp(List<double> curve, double xp)
    {
        double level = 0;
        for (var i = 0; i < curve.Count; i++)
        {
            if (xp >= curve[i]) level = i;
        }
        return level;
    }

    public static EngineStep GrantXp(EngineContext ctx, GameState state, string skill, double xp)
    {
        if (!ctx.Content.Settings.SkillsEnabled || xp <= 0) return EngineStep.Of(state);

        var current = state.Player.Skills.TryGetValue(skill, out var existing) && existing != null
            ? existing
            : new SkillState { Xp = 0, Level = 0 };
        var newXp = current.Xp + xp;
        var newLevel = SkillLevelForXp(ctx.Content.Settings.SkillLevelCurve, newXp);
        var effects = new List<Effect>();
        if (newLevel > current.Level)
        {
            var label = skill.Length == 0 ? "" : skill[..1].ToUpperInvariant() + skill[1..];
            effects.Add(Effect.Message("success", $"{label} level {Js.Num(newLevel)}!"));
        }

        var skills = new OrderedDictionary<string, SkillState>(state.Player.Skills)
        {
            [skill] = new SkillState { Xp = newXp, Level = newLevel },
        };
        return new EngineStep(state with { Player = state.Player with { Skills = skills } }, effects);
    }

    public static double SkillLevel(GameState state, string skill) =>
        state.Player.Skills.TryGetValue(skill, out var s) && s != null ? s.Level : 0;

    /// <summary>Farming levels add bonus yield: +1 per 4 levels.</summary>
    public static double FarmingYieldBonus(GameState state) =>
        Math.Floor(SkillLevel(state, "farming") / 4);
}
