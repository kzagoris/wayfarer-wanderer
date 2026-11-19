// From Passenger 2044e49b src/Passenger.App/IdeAction.cs, MIT (notices/Passenger/LICENSE).
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell;

/// <summary>Where a menu lists an action: the menu's path from the menu bar ("Git", "Navigate/Bookmarks"), the run of
/// items between two separators it is in, and its place in that run.</summary>
public sealed record MenuPlace(string Path, int Group, int Order);

/// <summary>One of the window's actions, declared once by the feature it belongs to: its id (what keymaps bind and agents
/// name), its title and group as Find Action lists it, where the menus list it, what it does, and whether it can run
/// where it is asked to. Its keys, its menu items, Find Action and an agent's ide_action all run it the same way.</summary>
public sealed record IdeAction(string Id, string Title, string Group, Action<ActionContext> Run)
{
    /// <summary>Null when it can run in this context, else why not in words for the status bar and an agent ("No file is
    /// open"). Its menu items are disabled and Find Action greys it while it says why not.</summary>
    public Func<ActionContext, string?> WhyNot { get; init; } = _ => null;

    public IReadOnlyList<MenuPlace> Menus { get; init; } = [];

    /// <summary>Its title in a menu opened where it would run, when that says more than <see cref="Title"/> (Unpin Tab on
    /// a pinned tab, Close on a tab's own menu); Find Action lists it by its title.</summary>
    public Func<ActionContext, string>? TitleIn { get; init; }

    /// <summary>For an action that turns something on and off (Soft-Wrap), whether it is on where it would run: its
    /// menu item shows a check then.</summary>
    public Func<ActionContext, bool>? IsChecked { get; init; }

    /// <summary>Whether its keys run it while the terminal has them; every other key there is the shell's.</summary>
    public bool FromTerminal { get; init; }

    /// <summary>An editor's own action (a movement, typing, the clipboard): its keys run it only in the editor with the
    /// keys, and anywhere else go on to whatever has them; a menu runs it where the keys are, a text box included.</summary>
    public bool InEditor { get; init; }

    /// <summary>Other names Find Action finds it by, such as an older name a person may still type (Split Vertically
    /// for Split Right).</summary>
    public IReadOnlyList<string> Words { get; init; } = [];

    /// <summary>The action whose work this one starts, which <see cref="ActionWork"/> holds it under while it works: its own
    /// unless it shares another's (Rebuild Solution is a build, as Build Solution is).</summary>
    public string? Works { get; init; }

    /// <summary>The id its work is held under.</summary>
    public string WorkId => Works ?? Id;

    /// <summary>For an action whose keys are another's where it applies, one key running two actions told apart by
    /// where it is pressed (the Explorer's Compare Files takes the editor's Duplicate keys in Visual Studio's keymap):
    /// that action, whose keys its menu items and Find Action show. Null with keys of its own, or none.</summary>
    public Func<string?>? KeysOf { get; init; }

    public bool CanRun(ActionContext context) => WhyNot(context) is null;

    /// <summary>Its title in a menu opened in this context.</summary>
    public string TitleWhere(ActionContext context) => TitleIn?.Invoke(context) ?? Title;
}

/// <summary>Where an action runs: the editor and its caret, and what has the keys (the editor, a text box, a tool window).
/// Made as the action is asked for, before a menu or popup takes the keys.</summary>
public sealed class ActionContext(CodeEditor editor, object? focused)
{
    /// <summary>The window's code editor.</summary>
    public CodeEditor Editor => editor;

    /// <summary>The editor's caret.</summary>
    public TextPosition Caret => editor.CaretPosition;

    /// <summary>What had the keys as the action was asked for.</summary>
    public object? Focused => focused;
}
