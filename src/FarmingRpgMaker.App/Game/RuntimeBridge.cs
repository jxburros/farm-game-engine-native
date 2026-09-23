using FarmEngine.Core;
using FarmEngine.Runtime;

namespace FarmingRpgMaker.App.Game;

/// <summary>Sound output for play mode (effects → synthesized SFX presets).</summary>
public interface IGameAudio
{
    void PlayForEffect(Effect effect);
}

/// <summary>Silent audio (tests, headless).</summary>
public sealed class NullGameAudio : IGameAudio
{
    public static readonly NullGameAudio Instance = new();

    public void PlayForEffect(Effect effect)
    {
    }
}

/// <summary>
/// Play-mode audio over the runtime <see cref="AudioManager"/>: effects map to SFX presets via
/// <see cref="Audio.SfxForEffect"/> (web <c>sfxForEffect</c>). Without a backend it is silent;
/// the manager still tracks settings so a platform backend can be attached later.
/// </summary>
public sealed class RuntimeGameAudio(AudioManager manager) : IGameAudio, IDisposable
{
    public AudioManager Manager { get; } = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <summary>The effect names played so far (diagnostics/tests), newest last, bounded.</summary>
    public List<string> Played { get; } = [];

    public void PlayForEffect(Effect effect)
    {
        if (Audio.SfxForEffect(effect) is { } sfx)
        {
            Played.Add(sfx);
            if (Played.Count > 64)
            {
                Played.RemoveAt(0);
            }

            Manager.Play(sfx);
        }
    }

    public void Dispose() => Manager.Dispose();
}
