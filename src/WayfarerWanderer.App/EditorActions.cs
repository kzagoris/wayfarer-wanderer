namespace WayfarerWanderer.App;

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

public sealed class EditorActions
{
    public const string ToggleBreakpoint = "app.debug.toggleBreakpoint";
    private readonly Dictionary<string, EditorAction> actions;

    public EditorActions(Func<bool> hasDocument, Action toggle)
    {
        var breakpoint = new EditorAction(ToggleBreakpoint, "Toggle Breakpoint", hasDocument, toggle);
        actions = new() { [breakpoint.Id] = breakpoint };
    }

    public EditorAction this[string id] => actions[id];

    public IEnumerable<EditorAction> Find(string query) =>
        actions.Values.Where(action => action.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

    public bool TryExecute(string? id) =>
        id != null && actions.TryGetValue(id, out var action) && action.TryExecute();
}
