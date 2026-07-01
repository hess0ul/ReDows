using System.Security.Principal;

namespace ReDows.Providers.Windows;

/// <summary>
/// Whether this process runs with administrator rights. Creating a volume shadow copy
/// (to rescue locked files during a copy) needs elevation; when it is absent, ReDows falls
/// back to live reads and reports the locked files rather than failing the whole run.
/// </summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
