using System.Text.Json.Serialization;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// One entry of a replay input log. JSON shape matches the TS union:
/// <c>{"kind":"command","command":{…}}</c> / <c>{"kind":"tick","ticks":N}</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CommandInput), "command")]
[JsonDerivedType(typeof(TickInput), "tick")]
public abstract record ReplayInput
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed record CommandInput(Command Command) : ReplayInput { [JsonIgnore] public override string Kind => "command"; }

public sealed record TickInput(double Ticks) : ReplayInput { [JsonIgnore] public override string Kind => "tick"; }

public sealed record ReplayResult(GameState State, string Hash, List<Effect> Effects);

/// <summary>
/// Replay harness (port of replay.ts): run a scripted input log through the
/// engine and hash the final state. Golden replay tests fail on any
/// nondeterminism.
/// </summary>
public static class Replay
{
    public static ReplayResult RunReplay(EngineContext ctx, GameState initialState, IEnumerable<ReplayInput> inputs)
    {
        var state = initialState;
        var effects = new List<Effect>();

        foreach (var input in inputs)
        {
            if (input is TickInput tick)
            {
                var step = Engine.AdvanceTick(ctx, state, tick.Ticks);
                state = step.State;
                effects.AddRange(step.Effects);
            }
            else if (input is CommandInput command)
            {
                var step = Engine.ApplyCommand(ctx, state, command.Command);
                state = step.State;
                effects.AddRange(step.Effects);
            }
        }

        return new ReplayResult(state, Hash.HashState(state), effects);
    }

    /// <summary>TS <c>command(cmd)</c>.</summary>
    public static ReplayInput Cmd(Command command) => new CommandInput(command);

    /// <summary>TS <c>command(cmd)</c> under its PascalCase name (alias of <see cref="Cmd"/>).</summary>
    public static ReplayInput Command(Command command) => new CommandInput(command);

    /// <summary>TS <c>ticks(count)</c>.</summary>
    public static ReplayInput Ticks(double count) => new TickInput(count);
}
