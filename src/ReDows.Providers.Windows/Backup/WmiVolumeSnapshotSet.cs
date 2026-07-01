using ReDows.Providers.Windows.Settings;

namespace ReDows.Providers.Windows.Backup;

/// <summary>
/// Real volume shadow copies through WMI (<c>Win32_ShadowCopy</c>), driven via PowerShell so
/// no extra dependency is needed. One <c>ClientAccessible</c> shadow is created per volume on
/// first use and deleted on <see cref="Dispose"/>. Creating a shadow needs elevation; without
/// it the create fails and <see cref="DeviceRootForVolumeOf"/> returns <c>null</c> (cached so it
/// is not retried per file). Not thread-safe — the copy engine reads sequentially.
/// </summary>
public sealed class WmiVolumeSnapshotSet : IVolumeSnapshotSet
{
    // volume root (e.g. "C:\") → device root, or null when creation failed (both cached).
    private readonly Dictionary<string, string?> _deviceByVolume = new(StringComparer.OrdinalIgnoreCase);

    // volume root → the shadow ID we created, so we can delete exactly those on dispose.
    private readonly Dictionary<string, string> _shadowIdByVolume = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string? DeviceRootForVolumeOf(string windowsPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var root = Path.GetPathRoot(windowsPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var volume = root.EndsWith('\\') ? root : root + '\\';
        if (_deviceByVolume.TryGetValue(volume, out var cached))
        {
            return cached;
        }

        var (id, device) = CreateShadow(volume);
        _deviceByVolume[volume] = device;
        if (id is not null && device is not null)
        {
            _shadowIdByVolume[volume] = id;
        }

        return device;
    }

    /// <summary>
    /// Ask Windows to make a ClientAccessible shadow of <paramref name="volume"/> and return its
    /// shadow ID and device root. The PowerShell uses only single-quoted literals and the -f
    /// operator (no double quotes), since <see cref="PowerShellQuery"/> wraps the command in quotes.
    /// </summary>
    private static (string? Id, string? Device) CreateShadow(string volume)
    {
        var command = string.Join("; ",
            $"$r = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{{ Volume = '{volume}'; Context = 'ClientAccessible' }}",
            "if ($r.ReturnValue -ne 0) { exit 1 }",
            "$sc = Get-CimInstance Win32_ShadowCopy -Filter ('ID=''{0}''' -f $r.ShadowID)",
            "$r.ShadowID + '|' + $sc.DeviceObject");

        var (output, _) = PowerShellQuery.Run(command);
        if (output is null)
        {
            return (null, null);
        }

        var line = output.Trim();
        var bar = line.IndexOf('|', StringComparison.Ordinal);
        if (bar <= 0 || bar == line.Length - 1)
        {
            return (null, null);
        }

        return (line[..bar], line[(bar + 1)..]);
    }

    private static void DeleteShadow(string shadowId)
    {
        var command = string.Join("; ",
            $"$id = '{shadowId}'",
            "Get-CimInstance Win32_ShadowCopy -Filter ('ID=''{0}''' -f $id) | Remove-CimInstance -Confirm:$false");

        // Best effort: a leftover ClientAccessible shadow is harmless and Windows recycles it.
        PowerShellQuery.Run(command);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var shadowId in _shadowIdByVolume.Values)
        {
            DeleteShadow(shadowId);
        }
    }
}
