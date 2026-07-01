namespace ReDows.Gui.ViewModels;

/// <summary>One volume line on the Home screen.</summary>
public sealed record VolumeRow(string Status, string Mount, string Detail, bool Scannable);

/// <summary>One user-profile line on the Home screen.</summary>
public sealed record ProfileRow(string UserName, string HiveState, string RootPath);
