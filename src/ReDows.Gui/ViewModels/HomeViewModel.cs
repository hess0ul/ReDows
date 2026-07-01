using System.Collections.ObjectModel;
using ReDows.Gui.Context;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The Home screen's brain: it asks the (injected, read-only) context source for the machine
/// context and shapes it into display rows plus a one-line summary. Pure enough to test off a
/// fake source — it touches no WPF type. A provider failure becomes <see cref="Error"/>, never a crash.
/// </summary>
public sealed class HomeViewModel(IContextSource source) : ViewModelBase
{
    private string _summary = "";
    private string? _error;
    private int _notesCount;

    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    public ObservableCollection<ProfileRow> Profiles { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public int NotesCount
    {
        get => _notesCount;
        private set => Set(ref _notesCount, value);
    }

    public void Load()
    {
        try
        {
            var context = source.Load();

            Volumes.Clear();
            foreach (var volume in context.AllVolumes)
            {
                var mount = volume.PreferredMountPath ?? "(no mount point)";
                var size = volume.TotalBytes is { } bytes ? $"{bytes / (1024.0 * 1024 * 1024):F1} GB" : "?";
                var detail = $"{volume.DriveKind}, {volume.FileSystemFormat ?? "?"}, {size}";
                Volumes.Add(new VolumeRow(volume.Scannable ? "scan" : "excluded", mount, detail, volume.Scannable));
            }

            Profiles.Clear();
            foreach (var profile in context.Context.Profiles)
            {
                var hive = profile.HiveResolved ? "hive resolved" : "hive unread (degraded)";
                Profiles.Add(new ProfileRow(profile.UserName, hive, profile.RootPath));
            }

            var scannable = context.AllVolumes.Count(v => v.Scannable);
            var excluded = context.AllVolumes.Count - scannable;
            NotesCount = context.Notes.Count;
            Summary = $"{scannable} volume(s) to scan · {excluded} excluded · " +
                      $"{context.Context.Profiles.Count} profile(s) · {context.Context.Orphans.Count} orphan folder(s)";
            Error = null;
        }
        catch (Exception ex)
        {
            Error = $"Could not read this PC's context: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
