namespace ReDows.Core.Scanning;

/// <summary>
/// The single reparse-point traversal policy, shared by the walker (recursion)
/// and the engine (classification) so they can never disagree. Symlinks and
/// junctions are never followed (cycles, double counting — the target volume is
/// covered by its own GUID root). Cloud placeholder directories ARE traversed:
/// the OneDrive root is itself a reparse point, and enumerating a placeholder
/// directory hydrates nothing (this block never reads file contents).
/// </summary>
public static class ReparsePoints
{
    /// <summary>FILE_ATTRIBUTE_REPARSE_POINT.</summary>
    public const int ReparsePointAttribute = 0x400;

    public const uint SymbolicLink = 0xA000000C; // IO_REPARSE_TAG_SYMLINK
    public const uint MountPoint = 0xA0000003;   // IO_REPARSE_TAG_MOUNT_POINT (junctions, folder-mounted volumes)
    public const uint AppExecLink = 0x8000001B;  // IO_REPARSE_TAG_APPEXECLINK (Store app stubs)

    /// <summary>
    /// Sentinel for an entry whose reparse attribute is set but whose tag could
    /// not be read: fail closed in the non-traversal direction (never recurse
    /// into a possible cycle on hope). Not a valid Microsoft tag value, and not
    /// a name surrogate, so a file carrying it still flows to the ruleset.
    /// </summary>
    public const uint UnreadableTag = 0x0000_FFFF;

    /// <summary>
    /// The cloud-files family used by OneDrive Files-On-Demand:
    /// IO_REPARSE_TAG_CLOUD (0x9000001A) through _F (0x9000F01A). Deliberately
    /// NOT the whole 0x9000xxxx range — ProjFS (0x9000001C, VFS-for-Git) and
    /// container isolation (WCI) live there too, and enumerating a ProjFS root
    /// drives its provider (can hang on a dead one, makes IT materialize
    /// placeholders). Unknown tags fall to the counted non-traversed path.
    /// </summary>
    public static bool IsCloudPlaceholder(uint tag) => (tag & 0xFFFF_0FFF) == 0x9000_001A;

    /// <summary>
    /// Name-surrogate tags (bit 0x20000000): the object stands in for another
    /// path (symlink, junction). A reparse-tagged FILE without this bit is real
    /// data — WOF-compressed (compact.exe), deduplicated, cloud placeholder —
    /// and must be classified by the ruleset, never downgraded to a "link".
    /// </summary>
    public static bool IsNameSurrogate(uint tag) => (tag & 0x2000_0000) != 0;

    /// <summary>True when a directory carrying this tag must be recursed into.</summary>
    public static bool ShouldTraverse(uint tag) => tag == 0 || IsCloudPlaceholder(tag);

    public static string Describe(uint tag) => tag switch
    {
        0 => "none",
        SymbolicLink => "symlink",
        MountPoint => "junction/mount point",
        AppExecLink => "app execution alias",
        UnreadableTag => "unreadable reparse tag",
        _ when IsCloudPlaceholder(tag) => "cloud placeholder",
        _ => $"reparse tag 0x{tag:X8}",
    };
}
