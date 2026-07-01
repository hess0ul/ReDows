using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ReDows.Gui.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base (no MVVM dependency) so view-models stay plain and testable.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Notify that a (usually computed) property changed, e.g. one derived from other state.</summary>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
