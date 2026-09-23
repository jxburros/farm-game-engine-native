using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Core;

namespace FarmEngine.Runtime;

// Port of audio.ts. The TS AudioManager drives WebAudio directly; here the
// manager keeps the same settings/unlock/mute semantics and hands playback
// to a host-provided IAudioBackend. Games are audible with zero assets —
// every SFX is a synthesized tone description (the audio analogue of the
// rectangle fallback), which SfxPreset.Render turns into PCM for backends
// that just play buffers.

/// <summary>Persisted volume settings (TS <c>AudioSettings</c>).</summary>
public sealed record AudioSettings
{
    public double Master { get; init; } = 0.8;
    public double Sfx { get; init; } = 0.9;
    public double Music { get; init; } = 0.6;
    public bool Muted { get; init; } = false;
}

/// <summary>Oscillator waveforms (WebAudio <c>OscillatorType</c> names).</summary>
public static class Waveforms
{
    public const string Sine = "sine";
    public const string Square = "square";
    public const string Triangle = "triangle";
    public const string Sawtooth = "sawtooth";
}

/// <summary>
/// A synthesized SFX: one oscillator stepping through <see cref="Steps"/>
/// (Hz, each held <c>Duration / Steps.Count</c> seconds), with a gain envelope
/// starting at <see cref="Gain"/> and ramping exponentially to 0.001 over
/// <see cref="Duration"/>; the oscillator stops 20 ms after that.
/// </summary>
/// <param name="Waveform">One of <see cref="Waveforms"/>.</param>
public sealed record SfxPreset(IReadOnlyList<double> Steps, double Duration, string Waveform, double Gain)
{
    /// <summary>Total sounding time in seconds (TS <c>oscillator.stop(now + duration + 0.02)</c>).</summary>
    public double TotalDuration => Duration + 0.02;

    /// <summary>
    /// Render mono PCM samples in [-1, 1] at <paramref name="sampleRate"/>, before
    /// the SFX/master volumes (the backend applies those).
    /// </summary>
    public float[] Render(int sampleRate = 44100)
    {
        var count = (int)Math.Ceiling(TotalDuration * sampleRate);
        var samples = new float[count];
        var stepLength = Duration / Steps.Count;
        var phase = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / sampleRate;
            var step = Math.Min(Steps.Count - 1, (int)Math.Floor(t / stepLength));
            phase += Steps[step] / sampleRate;
            phase -= Math.Floor(phase);
            var wave = Waveform switch
            {
                Waveforms.Square => phase < 0.5 ? 1.0 : -1.0,
                Waveforms.Triangle => 1 - 4 * Math.Abs(phase - 0.5),
                Waveforms.Sawtooth => 2 * phase - 1,
                _ => Math.Sin(2 * Math.PI * phase),
            };
            // exponentialRampToValueAtTime(0.001, duration), then held.
            var envelope = t >= Duration ? 0.001 : Gain * Math.Pow(0.001 / Gain, t / Duration);
            samples[i] = (float)(wave * envelope);
        }
        return samples;
    }
}

/// <summary>Minimal key-value persistence (the TS code's <c>localStorage</c>).</summary>
public interface IKeyValueStore
{
    string? GetItem(string key);
    void SetItem(string key, string value);
}

/// <summary>Process-lifetime store (tests, or hosts without persistence).</summary>
public sealed class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);
    public string? GetItem(string key) => _items.TryGetValue(key, out var value) ? value : null;
    public void SetItem(string key, string value) => _items[key] = value;
}

/// <summary>
/// The host's audio output. All calls come from the game loop thread;
/// implementations should never throw (audio failures must not break play).
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>Apply bus volumes (master is 0 while muted).</summary>
    void SetVolumes(double master, double sfx, double music);
    /// <summary>Play a synthesized effect on the SFX bus.</summary>
    void PlayTone(string name, SfxPreset preset);
    /// <summary>Loop music from a URL/path (data: URLs from packs too); null stops music. Crossfade is optional.</summary>
    void PlayMusic(string? url);
    /// <summary>Pause all output (window hidden / minimized).</summary>
    void Suspend();
    /// <summary>Resume output.</summary>
    void Resume();
}

/// <summary>
/// Audio manager (M7): synthesized SFX presets, optional music, persisted
/// volume settings, mute-on-blur. All methods are safe to call headless (no
/// backend → no-ops). Call <see cref="Unlock"/> once the host is ready to make
/// sound (the browser version waits for a user gesture).
/// </summary>
public sealed class AudioManager : IDisposable
{
    public const string SettingsKey = "farm-audio-settings";

    public static readonly AudioSettings DefaultSettings = new();

    private readonly IKeyValueStore? _storage;
    private IAudioBackend? _backend;
    private bool _unlocked;

    public AudioSettings Settings { get; private set; }

    public AudioManager(IKeyValueStore? storage = null, IAudioBackend? backend = null)
    {
        _storage = storage;
        _backend = backend;
        Settings = DefaultSettings;
        try
        {
            var raw = _storage?.GetItem(SettingsKey);
            if (!string.IsNullOrEmpty(raw)) Settings = MergeSettings(DefaultSettings, raw);
        }
        catch (Exception)
        {
            // corrupt settings → defaults
            Settings = DefaultSettings;
        }
    }

    /// <summary><c>{ ...DEFAULT_SETTINGS, ...JSON.parse(raw) }</c> over the known keys.</summary>
    private static AudioSettings MergeSettings(AudioSettings defaults, string raw)
    {
        if (JsonNode.Parse(raw) is not JsonObject obj) return defaults;
        static double Num(JsonObject o, string key, double fallback) =>
            o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : fallback;
        static bool Bool(JsonObject o, string key, bool fallback) =>
            o[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : fallback;
        return new AudioSettings
        {
            Master = Num(obj, "master", defaults.Master),
            Sfx = Num(obj, "sfx", defaults.Sfx),
            Music = Num(obj, "music", defaults.Music),
            Muted = Bool(obj, "muted", defaults.Muted),
        };
    }

    public bool IsUnlocked => _unlocked;

    /// <summary>Attach/replace the output backend (e.g. once the platform audio device opens).</summary>
    public void AttachBackend(IAudioBackend? backend)
    {
        _backend = backend;
        if (_unlocked) ApplyVolumes();
    }

    /// <summary>Start/resume output (TS: create/resume the AudioContext on a user gesture).</summary>
    public void Unlock()
    {
        if (_backend is null) return;
        if (!_unlocked)
        {
            _unlocked = true;
            ApplyVolumes();
        }
        _backend.Resume();
    }

    private void ApplyVolumes() => _backend?.SetVolumes(Settings.Muted ? 0 : Settings.Master, Settings.Sfx, Settings.Music);

    /// <summary>Patch settings (TS <c>configure(patch)</c>), apply and persist them.</summary>
    public void Configure(double? master = null, double? sfx = null, double? music = null, bool? muted = null)
    {
        Settings = Settings with
        {
            Master = master ?? Settings.Master,
            Sfx = sfx ?? Settings.Sfx,
            Music = music ?? Settings.Music,
            Muted = muted ?? Settings.Muted,
        };
        if (_unlocked) ApplyVolumes();
        try
        {
            _storage?.SetItem(SettingsKey, JsonSerializer.Serialize(Settings, FarmEngine.Json.JsonDefaults.Options));
        }
        catch (Exception)
        {
            // storage full — settings just don't persist
        }
    }

    /// <summary>Play a named synthesized effect. Unknown names are ignored; silent until unlocked or while muted.</summary>
    public void Play(string name)
    {
        if (!Audio.SfxPresets.TryGetValue(name, out var preset) || !_unlocked || _backend is null || Settings.Muted) return;
        _backend.PlayTone(name, preset);
    }

    /// <summary>Loop music from a URL (data: URLs from packs work too); null stops it.</summary>
    public void PlayMusic(string? url)
    {
        if (!_unlocked || _backend is null) return;
        _backend.PlayMusic(url);
    }

    /// <summary>Mute-on-blur: suspend while the window is hidden, resume (unless muted) when shown.</summary>
    public void SetVisible(bool visible)
    {
        if (!_unlocked || _backend is null) return;
        if (!visible) _backend.Suspend();
        else if (!Settings.Muted) _backend.Resume();
    }

    public void Dispose()
    {
        _backend?.PlayMusic(null);
        _backend?.Dispose();
        _backend = null;
        _unlocked = false;
    }
}

/// <summary>SFX presets and the effect → sound mapping.</summary>
public static class Audio
{
    /// <summary>name → tone description (TS <c>SFX_PRESETS</c>).</summary>
    public static readonly IReadOnlyDictionary<string, SfxPreset> SfxPresets = new Dictionary<string, SfxPreset>(StringComparer.Ordinal)
    {
        ["ui"] = new([660], 0.06, Waveforms.Sine, 0.25),
        ["success"] = new([523, 659], 0.12, Waveforms.Triangle, 0.3),
        ["error"] = new([196, 147], 0.16, Waveforms.Square, 0.18),
        ["till"] = new([140, 100], 0.1, Waveforms.Triangle, 0.35),
        ["water"] = new([420, 340, 300], 0.18, Waveforms.Sine, 0.25),
        ["chop"] = new([180, 90], 0.09, Waveforms.Square, 0.28),
        ["harvest"] = new([392, 523, 659], 0.2, Waveforms.Triangle, 0.32),
        ["coin"] = new([988, 1319], 0.11, Waveforms.Square, 0.2),
        ["quest"] = new([523, 659, 784, 1047], 0.4, Waveforms.Triangle, 0.3),
        ["sleep"] = new([330, 262, 196], 0.5, Waveforms.Sine, 0.25),
        ["step"] = new([220], 0.03, Waveforms.Triangle, 0.08),
    };

    /// <summary>Map an engine effect to an SFX preset name (data-driven hook for hosts).</summary>
    public static string? SfxForEffect(Effect effect) => effect switch
    {
        SoundEffect sound => sound.Id,
        MessageEffect message => message.Level == MessageLevels.Error ? "error" : message.Level == MessageLevels.Success ? "success" : null,
        CropHarvestedEffect => "harvest",
        QuestCompletedEffect => "quest",
        DayStartedEffect => "sleep",
        SceneChangedEffect => "ui",
        _ => null,
    };
}
