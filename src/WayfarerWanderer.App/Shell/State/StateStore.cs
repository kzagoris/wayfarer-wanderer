// From Passenger 2044e49b src/Passenger.App/State/StateStore.cs, MIT (notices/Passenger/LICENSE).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WayfarerWanderer.App.Shell.Settings;

namespace WayfarerWanderer.App.Shell.State;

/// <summary>Wayfarer's state folder: the settings and the documents every window shares, and a folder of documents for each
/// project (<see cref="ProjectState"/>). One store per root, shared by the windows that use it, written a moment after a
/// change and only through it.</summary>
public sealed class StateStore
{
    public const string SettingsFile = "settings.json";

    private static readonly Dictionary<string, StateStore> Shared = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (ProjectState Documents, int Holds)> projects = new(StringComparer.Ordinal);
    private readonly string key;
    private int users;

    private StateStore(string root, string key)
    {
        Root = root;
        this.key = key;
        Settings = SettingsStore.Kept(new StateDocument<AppSettings>(Path.Combine(root, SettingsFile)));
        Layout = new(Path.Combine(root, "layout.json"));
        Placement = new(Path.Combine(root, "windowPlacement.json"));
        // Before this process writes any: a change is written a moment after it is made, and only through this store.
        UsedBefore = File.Exists(Path.Combine(root, SettingsFile)) || Directory.Exists(Path.Combine(root, "workspaces"));
    }

    /// <summary>Whether an earlier run left state here: a new state folder's first start asks the look first.</summary>
    public bool UsedBefore { get; }

    /// <summary>A state folder other than the person's, for a test or a second, separate setup.</summary>
    public const string Variable = "WAYFARER_SETTINGS";

    public static string DefaultRoot => Named ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Passenger.Core.Product.Name);

    public static string? Named => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } folder ? folder : null;

    public string Root { get; }

    public SettingsStore Settings { get; }

    /// <summary>The tool windows' layout as it was changed last, which the window opens on.</summary>
    public StateDocument<Docking.DockLayout> Layout { get; }

    public StateDocument<WindowPlacementState> Placement { get; }

    public static StateStore Open(string root)
    {
        UiThread.Check();
        string key = Path.GetFullPath(root);
        if (!Shared.TryGetValue(key, out StateStore? store))
        {
            store = new StateStore(root, key);
            Shared[key] = store;
        }
        store.users++;
        return store;
    }

    public void Release()
    {
        UiThread.Check();
        if (--users > 0)
        {
            return;
        }
        Flush();
        Settings.Dispose();
        if (Shared.TryGetValue(key, out StateStore? open) && open == this)
        {
            Shared.Remove(key);
        }
    }

    public ProjectState Hold(string folder)
    {
        UiThread.Check();
        ProjectState documents = projects.TryGetValue(folder, out (ProjectState Documents, int Holds) held) ? held.Documents : new ProjectState(Root, folder);
        projects[folder] = (documents, held.Holds + 1);
        return documents;
    }

    public void LetGo(string folder)
    {
        UiThread.Check();
        if (!projects.TryGetValue(folder, out (ProjectState Documents, int Holds) held))
        {
            return;
        }
        if (held.Holds > 1)
        {
            projects[folder] = (held.Documents, held.Holds - 1);
            return;
        }
        projects.Remove(folder);
        held.Documents.Flush();
    }

    public IReadOnlyCollection<string> Folders => projects.Keys;

    public void Flush()
    {
        Settings.Flush();
        Layout.Flush();
        Placement.Flush();
        foreach ((ProjectState documents, _) in projects.Values)
        {
            documents.Flush();
        }
    }

    public static void LandWaiting() => StateFile.LandWaiting();

    /// <summary>A document written before versions were: nothing to move yet.</summary>
    public static void Unversioned(JsonObject document)
    {
    }
}

/// <summary>A project's own documents, in a folder of the state named for the project's folder.</summary>
public sealed class ProjectState
{
    internal ProjectState(string root, string folder)
    {
        Folder = FolderOf(root, folder);
    }

    public string Folder { get; }

    public static string FolderOf(string root, string folder)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..12];
        return Path.Combine(root, "workspaces", $"{Path.GetFileName(folder)}-{hash}");
    }

    public Task ReadAsync() => Task.CompletedTask;

    public void Flush()
    {
    }
}
