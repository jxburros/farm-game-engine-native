using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// EngineContext bundles immutable content with the hook bus (state.ts).
/// GameContent never changes during play.
/// </summary>
public sealed record EngineContext(GameContent Content, HookBus? Hooks = null);

/// <summary>Result of a reducer step: the next state plus host-facing effects.</summary>
public sealed record EngineStep(GameState State, List<Effect> Effects)
{
    public static EngineStep Of(GameState state) => new(state, []);
    public static EngineStep Of(GameState state, params Effect[] effects) => new(state, [.. effects]);
}
