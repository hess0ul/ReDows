using ReDows.Core.Modules;
using ReDows.Core.Scanning;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// One category row on the Scan screen: a module (games, photos…) and the user's chosen action. The
/// three <c>Is*</c> booleans back a row of radio buttons (keep / review / ignore) without a converter —
/// setting one to true moves <see cref="Action"/>, and <see cref="Action"/> re-raises all three so the
/// buttons stay in sync. <see cref="ToCategoryModule"/> binds the choice to the engine input.
/// </summary>
public sealed class ModuleRowViewModel : ViewModelBase
{
    private readonly ModuleDefinition _definition;
    private ModuleAction _action;

    public ModuleRowViewModel(ModuleDefinition definition)
    {
        _definition = definition;
        _action = definition.DefaultAction;
    }

    public string Name => _definition.Name;

    public string Label => _definition.Label;

    /// <summary>
    /// Human list of what this category matches, shown in the "?" tooltip: the file extensions
    /// (".jpg, .png, …") for an extension module, or the folder names for a folder module (games).
    /// </summary>
    public string Detects =>
        _definition.Extensions.Count > 0
            ? "File types: " + string.Join(", ", _definition.Extensions.Select(extension => "." + extension))
            : _definition.FolderNames.Count > 0
                ? "Folders named: " + string.Join(", ", _definition.FolderNames)
                : "(nothing)";

    public ModuleAction Action
    {
        get => _action;
        set
        {
            Set(ref _action, value);
            Raise(nameof(IsKeep));
            Raise(nameof(IsReview));
            Raise(nameof(IsIgnore));
        }
    }

    public bool IsKeep
    {
        get => _action == ModuleAction.Keep;
        set { if (value) Action = ModuleAction.Keep; }
    }

    public bool IsReview
    {
        get => _action == ModuleAction.Review;
        set { if (value) Action = ModuleAction.Review; }
    }

    public bool IsIgnore
    {
        get => _action == ModuleAction.Ignore;
        set { if (value) Action = ModuleAction.Ignore; }
    }

    public CategoryModule ToCategoryModule() => _definition.ToCategoryModule(_action);
}
