namespace ReDows.Core.Scanning;

/// <summary>
/// What the user chose to do with a whole category of items (games, media…). The
/// middle value REVIEW is the neutral default — the module does nothing and the
/// normal pipeline decides — so a category only takes effect when the user opts
/// into KEEP or IGNORE (default-to-review, invariant §2). KEEP upgrades every match
/// to a capture; IGNORE drops the re-acquirable bulk (but spares saves, see
/// <see cref="CategoryModule"/>).
/// </summary>
public enum ModuleAction
{
    /// <summary>Neutral default: leave matches to the normal pipeline (the module has no effect).</summary>
    Review,

    /// <summary>Keep every match (capture:user) — the user wants this category backed up.</summary>
    Keep,

    /// <summary>Ignore the re-acquirable bulk — but a save-named subtree stays REVIEW (forget-nothing).</summary>
    Ignore,
}

/// <summary>
/// A user-configured category module resolved for one scan: a detector (folder
/// names and/or file extensions) plus the chosen <see cref="ModuleAction"/>. The
/// data-driven, user-selectable sibling of the app-zones heuristics — here the
/// verdict is a user choice, not a fixed rule. The engine applies it AFTER the
/// ruleset and ONLY over a REVIEW verdict, exactly like the app-inventory zones, so
/// a keep/secret from the ruleset is never touched (a category ignore can never
/// swallow a captured file). Under IGNORE, a subtree whose name signals user data
/// (save*, profile*, userdata…, see <see cref="AppDataFolders"/>) stays REVIEW when
/// <see cref="SaveCarveBacks"/> is set: the user drops the game, never the saves.
/// </summary>
public sealed record CategoryModule
{
    public CategoryModule(
        string id,
        ModuleAction action,
        IReadOnlyList<string> folderNames,
        IReadOnlyList<string> extensions,
        bool saveCarveBacks)
    {
        if (string.IsNullOrEmpty(id) || !id.Contains(':'))
        {
            throw new ArgumentException(
                "category module ids must carry a ':' namespace (e.g. 'module:games') — ':' cannot appear " +
                "in rule or engine ids, so report attribution can never collide", nameof(id));
        }

        Id = id;
        Action = action;
        FolderNames = folderNames;
        Extensions = extensions;
        SaveCarveBacks = saveCarveBacks;
    }

    public string Id { get; }

    public ModuleAction Action { get; }

    /// <summary>Folder-name tokens (single-segment globs): a path segment equal to one of these marks a match.</summary>
    public IReadOnlyList<string> FolderNames { get; }

    /// <summary>File-extension tokens (no leading dot, lowercase): the leaf's extension is matched against these.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>Under IGNORE, keep a user-data-named subtree (save*/profile*/…) in REVIEW instead of ignoring it.</summary>
    public bool SaveCarveBacks { get; }
}
