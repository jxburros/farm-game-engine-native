using FarmEngine.Runtime;

namespace FarmEngine.Core.Tests.Runtime;

public class FixedTimestepAndAudioTests
{
    [Fact]
    public void FixedTimestepAccumulatesWholeTicksAndExposesAlpha()
    {
        var timestep = new FixedTimestep();
        Assert.Equal(0, timestep.Advance(0.02));
        Assert.Equal(0.4, timestep.Alpha, 9);
        Assert.Equal(1, timestep.Advance(0.04));
        Assert.Equal(0.2, timestep.Alpha, 9);
        // Frame deltas are capped at 250 ms (5 ticks at 20 t/s).
        Assert.Equal(5, timestep.Advance(10));
        timestep.Reset();
        Assert.Equal(0, timestep.Alpha);
    }

    private sealed class RecordingBackend : IAudioBackend
    {
        public List<string> Calls { get; } = [];
        public void SetVolumes(double master, double sfx, double music) => Calls.Add($"volumes {master} {sfx} {music}");
        public void PlayTone(string name, SfxPreset preset) => Calls.Add($"tone {name}");
        public void PlayMusic(string? url) => Calls.Add($"music {url}");
        public void Suspend() => Calls.Add("suspend");
        public void Resume() => Calls.Add("resume");
        public void Dispose() => Calls.Add("dispose");
    }

    [Fact]
    public void AudioSettingsPersistThroughTheInjectedStore()
    {
        var store = new InMemoryKeyValueStore();
        var audio = new AudioManager(store);
        Assert.Equal(AudioManager.DefaultSettings, audio.Settings);
        audio.Configure(master: 0.5, muted: true);
        Assert.Equal("{\"master\":0.5,\"sfx\":0.9,\"music\":0.6,\"muted\":true}", store.GetItem(AudioManager.SettingsKey));

        var reloaded = new AudioManager(store);
        Assert.Equal(0.5, reloaded.Settings.Master);
        Assert.True(reloaded.Settings.Muted);
    }

    [Fact]
    public void CorruptOrPartialSettingsFallBackToDefaults()
    {
        var store = new InMemoryKeyValueStore();
        store.SetItem(AudioManager.SettingsKey, "{not json");
        Assert.Equal(AudioManager.DefaultSettings, new AudioManager(store).Settings);
        store.SetItem(AudioManager.SettingsKey, "{\"sfx\":0.1}");
        Assert.Equal(AudioManager.DefaultSettings with { Sfx = 0.1 }, new AudioManager(store).Settings);
    }

    [Fact]
    public void PlaysPresetsOnlyAfterUnlockAndWhileUnmuted()
    {
        var backend = new RecordingBackend();
        var audio = new AudioManager(null, backend);
        audio.Play("coin");
        Assert.Empty(backend.Calls);

        audio.Unlock();
        audio.Play("coin");
        audio.Play("no-such-sound");
        audio.SetVisible(false);
        audio.SetVisible(true);
        audio.Configure(muted: true);
        audio.Play("coin");
        Assert.Equal(["volumes 0.8 0.9 0.6", "resume", "tone coin", "suspend", "resume", "volumes 0 0.9 0.6"], backend.Calls);
    }

    [Fact]
    public void MapsEffectsToSfx()
    {
        Assert.Equal("chop", Audio.SfxForEffect(new SoundEffect("chop")));
        Assert.Equal("error", Audio.SfxForEffect(new MessageEffect("error", "x")));
        Assert.Equal("success", Audio.SfxForEffect(new MessageEffect("success", "x")));
        Assert.Null(Audio.SfxForEffect(new MessageEffect("info", "x")));
        Assert.Equal("harvest", Audio.SfxForEffect(new CropHarvestedEffect("wheat", 1)));
        Assert.Equal("quest", Audio.SfxForEffect(new QuestCompletedEffect("q")));
        Assert.Equal("sleep", Audio.SfxForEffect(new DayStartedEffect(2, "spring", 1)));
        Assert.Equal("ui", Audio.SfxForEffect(new SceneChangedEffect("s", 0, 0)));
        Assert.Null(Audio.SfxForEffect(new PlayerMovedEffect(0, 0)));
    }

    [Fact]
    public void PresetsRenderToBoundedPcm()
    {
        var preset = Audio.SfxPresets["quest"];
        var samples = preset.Render(8000);
        Assert.Equal((int)Math.Ceiling(preset.TotalDuration * 8000), samples.Length);
        Assert.All(samples, s => Assert.InRange(s, -0.31f, 0.31f));
        Assert.Contains(samples, s => Math.Abs(s) > 0.1f);
    }
}
