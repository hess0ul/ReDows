namespace ReDows.Core;

/// <summary>Application identity, shared by every front-end (CLI, future GUI).</summary>
public static class ReDowsInfo
{
    public const string Name = "ReDows";

    /// <summary>Product version (defined in Directory.Build.props).</summary>
    public static string Version =>
        typeof(ReDowsInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
