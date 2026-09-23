namespace FarmingRpgMaker.Updates;

/// <summary>Loads and saves <see cref="UpdateSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Returns the saved settings, or defaults when missing/corrupt. Never throws.</summary>
    UpdateSettings Load();

    /// <summary>Persists the settings (atomically for file-backed stores).</summary>
    void Save(UpdateSettings settings);
}

/// <summary>Non-persistent store for tests and design-time previews.</summary>
public sealed class InMemorySettingsStore(UpdateSettings? initial = null) : ISettingsStore
{
    public UpdateSettings Current { get; private set; } = initial ?? new UpdateSettings();

    public int SaveCount { get; private set; }

    public UpdateSettings Load() => Current;

    public void Save(UpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;
        SaveCount++;
    }
}
