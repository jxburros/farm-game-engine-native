using System.Text.Json.Serialization;

namespace FarmEngine.Core;

/// <summary>
/// Effects are things the shell/renderer reacts to (port of effects.ts). They
/// never mutate simulation state — they are outputs of a step, consumed by the
/// host (toasts, sounds, camera snaps, …).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageEffect), "message")]
[JsonDerivedType(typeof(SceneChangedEffect), "sceneChanged")]
[JsonDerivedType(typeof(PlayerMovedEffect), "playerMoved")]
[JsonDerivedType(typeof(SoundEffect), "sound")]
[JsonDerivedType(typeof(QuestCompletedEffect), "questCompleted")]
[JsonDerivedType(typeof(CropHarvestedEffect), "cropHarvested")]
[JsonDerivedType(typeof(DayStartedEffect), "dayStarted")]
public abstract record Effect
{
    [JsonIgnore] public abstract string Type { get; }

    /// <summary>TS <c>message(level, text)</c>.</summary>
    public static Effect Message(string level, string text) => new MessageEffect(level, text);
}

public static class MessageLevels
{
    public const string Success = "success";
    public const string Error = "error";
    public const string Info = "info";
}

/// <summary><c>level</c> is one of <see cref="MessageLevels"/>.</summary>
public sealed record MessageEffect(string Level, string Text) : Effect { [JsonIgnore] public override string Type => "message"; }
public sealed record SceneChangedEffect(string SceneId, double X, double Y) : Effect { [JsonIgnore] public override string Type => "sceneChanged"; }
public sealed record PlayerMovedEffect(double X, double Y) : Effect { [JsonIgnore] public override string Type => "playerMoved"; }
public sealed record SoundEffect(string Id) : Effect { [JsonIgnore] public override string Type => "sound"; }
public sealed record QuestCompletedEffect(string QuestId) : Effect { [JsonIgnore] public override string Type => "questCompleted"; }
public sealed record CropHarvestedEffect(string CropType, double Quantity) : Effect { [JsonIgnore] public override string Type => "cropHarvested"; }
public sealed record DayStartedEffect(double Day, string Season, double Year) : Effect { [JsonIgnore] public override string Type => "dayStarted"; }
