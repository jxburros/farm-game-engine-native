namespace FarmingRpgMaker.App.Hosting;

/// <summary>
/// Composition root for the pluggable parts of the shell. The game/editor integration
/// replaces the placeholders here (in <see cref="CreateDefault"/>) with the real
/// implementations — nothing else in the shell needs to change.
/// </summary>
public sealed record ShellComposition(IGameSurfaceFactory GameSurfaceFactory, IProjectCommandHandler ProjectCommands)
{
    public static ShellComposition CreateDefault() =>
        new(new PlaceholderGameSurfaceFactory(), new PlaceholderProjectCommandHandler());
}
