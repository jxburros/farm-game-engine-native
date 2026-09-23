using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FarmingRpgMaker.App.Mvvm;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base for view models.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Raises change notifications for several properties.</summary>
    protected void OnPropertiesChanged(params ReadOnlySpan<string> propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}
