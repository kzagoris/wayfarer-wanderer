using Passenger.Editor;
using WayfarerWanderer.App.Shell;

namespace WayfarerWanderer.App;

/// <summary>One of the window's actions as its menus, Find Action and its keys see it: whether it can run now, and running
/// it when it can. A view of the <see cref="IdeAction"/> the window's <see cref="ActionRegistry"/> holds.</summary>
public sealed record EditorAction(string Id, string Title, Func<bool> Available, Action Execute)
{
    public bool TryExecute()
    {
        if (!Available())
            return false;

        Execute();
        return true;
    }
}

/// <summary>The window's actions, held in the shell's <see cref="ActionRegistry"/>: one registry for the menus, Find Action
/// and the keys. Ids shared with the shell's keymap presets (<see cref="AppActions"/>) take the presets' shortcuts.</summary>
public sealed class EditorActions
{
    public const string ToggleBreakpoint = AppActions.ToggleBreakpoint;
    public const string Build = AppActions.Build;
    public const string RunTests = AppActions.RunTests;
    public const string Run = AppActions.RunProgram;
    public const string Debug = AppActions.Debug;
    public const string Continue = AppActions.Resume;
    public const string StepInto = AppActions.StepInto;
    public const string StepOver = AppActions.StepOver;
    public const string StepOut = AppActions.StepOut;
    public const string OpenWorkspace = AppActions.OpenFolder;
    public const string OpenSource = AppActions.Open;
    public const string SaveSource = AppActions.Save;
    public const string Stop = AppActions.Stop;
    public const string NewProject = AppActions.NewProject;
    public const string ProjectStructure = AppActions.ProjectStructure;
    public const string ChooseRunJdk = "wayfarer.chooseRunJdk";

    /// <summary>The group Find Action lists Wayfarer's own actions under.</summary>
    private const string Group = "Wayfarer";

    private readonly Dictionary<string, EditorAction> views = new(StringComparer.Ordinal);

    public EditorActions(Func<bool> hasDocument, Action toggle, Func<ActionContext> context, Func<Keymap> keymap)
    {
        Registry = new ActionRegistry(context, keymap, new ActionWork());
        Add(new EditorAction(ToggleBreakpoint, "Toggle Breakpoint", hasDocument, toggle));
    }

    /// <summary>The registry every action is in.</summary>
    public ActionRegistry Registry { get; }

    public void Add(EditorAction action)
    {
        if (views.ContainsKey(action.Id))
        {
            throw new ArgumentException($"The action '{action.Id}' is already registered.", nameof(action));
        }
        Registry.Add(new IdeAction(action.Id, action.Title, Group, _ => action.Execute())
        {
            WhyNot = _ => action.Available() ? null : $"{action.Title} is not available here",
        });
        views[action.Id] = action;
    }

    public EditorAction this[string id] => views[id];

    /// <summary>The actions a query finds: Wayfarer's own, and those others put in the registry (the dock's Show actions).</summary>
    public IEnumerable<EditorAction> Find(string query) =>
        Registry.Find(query).Select(found => views.GetValueOrDefault(found.Id) ?? View(found));

    public bool TryExecute(string? id) =>
        id != null && Registry[id] is not null && Registry.Run(id) is null;

    private EditorAction View(IdeAction action) =>
        new(action.Id, action.Title, () => action.CanRun(Registry.Context()), () => Registry.Run(action.Id));
}
