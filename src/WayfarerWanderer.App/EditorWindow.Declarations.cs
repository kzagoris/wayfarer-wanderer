// From Passenger 8a1df7e4 src/Passenger.App/CSharp/LinkAssist.cs and MainWindow.Links.cs (WrittenLink), MIT
// (notices/Passenger/LICENSE): Go to Declaration asks the Java or Kotlin server, as upstream's code assist asks its C# one,
// and follows an address written there where the server declares nothing; its hover card and the consoles' links are cut.
using System.Text.Json;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>A web address written in a file's comment or string, which Go to Declaration opens in the browser.</summary>
public sealed record WebLink(Uri Address);

/// <summary>A file address written in a file's comment or string on this machine: its file opened, its folder shown.</summary>
public sealed record WrittenFileLink(string Path);

/// <summary>Go to Declaration (F12, Ctrl+click, Cmd+click on the Mac): where the Java or Kotlin server says what the caret is
/// on is declared, else, where it declares nothing, an address written in a comment or string at the caret followed (a web
/// address in the browser, a file's opened). While the click's keys are held over a name the server declares, or an
/// address, it is underlined and the pointer takes the hand (<see cref="CodeEditor.ClickReach"/>).</summary>
public sealed partial class EditorWindow
{
    private readonly List<Uri> browsed = [];

    /// <summary>Every address the window opened in the person's browser, in order, kept since the headless platform's
    /// launcher opens nothing.</summary>
    public IReadOnlyList<Uri> Browsed => browsed;

    /// <summary>Finds a declaration in place of the language servers (a file and a place in it, or null for none): for
    /// tests, which run none.</summary>
    public Func<string, TextPosition, CancellationToken, Task<(string Path, TextPosition At)?>>? DeclarationFinder { get; set; }

    /// <summary>What an address written in a file's comment or string opens (<see cref="DocumentDecorations.Addresses"/>):
    /// a web address in the browser, a file address on this machine its file or folder once followed, so drawing it asks
    /// nothing of the disk (<see cref="FoundPaths.OfFileAddress"/>); null for any other, which stays text.</summary>
    public static readonly Func<string, object?> WrittenLink = address => WebAddresses.Parse(address) is not { } uri ? null
        : uri.IsFile ? (FoundPaths.OfFileAddress(uri) is { } path ? new WrittenFileLink(path) : null)
        : new WebLink(uri);

    private void AddDeclarations()
    {
        Actions.Registry.Add(new IdeAction(AppActions.GoToDeclaration, "Go to Declaration", NavigateGroup, _ => IdeErrors.Observe(GoToDeclarationAsync()))
        {
            WhyNot = _ => SourcePath is null || !Editor.IsVisible ? "No file is open" : null,
            Words = ["Go to Definition", "Declaration", "Definition"],
        });
        // A mouse gesture's action (Ctrl+click's), which the editor hands the window with its caret where it was clicked.
        Editor.ActionRequested += (_, action) => Actions.TryExecute(action);
        Editor.ClickReach = (action, offset, cancel) => action == AppActions.GoToDeclaration ? ReachAsync(offset, cancel) : Task.FromResult<TextRange?>(null);
    }

    /// <summary>Go to Declaration at the caret: the server's declaration, else the address there followed, else the status
    /// line says there is nothing to go to.</summary>
    private async Task GoToDeclarationAsync()
    {
        if (SourcePath is not { } path)
        {
            return;
        }
        TextDocument text = Editor.Document;
        int offset = Editor.Selections.Primary.Caret;
        (string Path, TextPosition At)? found = await DeclarationAsync(path, Editor.CaretPosition, startServer: true, semanticLifetime.Token);
        if (SourcePath != path || !ReferenceEquals(Editor.Document, text) || Editor.Selections.Primary.Caret != offset)
        {
            // The person moved on while the server answered.
            return;
        }
        if (found is { } declared)
        {
            NavigateTo(declared.Path, declared.At);
        }
        else if (Editor.LinkOn(offset) is { } link)
        {
            FollowLink(link.Target);
        }
        else
        {
            StatusText = Language(path) is null ? $"Nothing to go to: {Path.GetFileName(path)} has no declarations Wayfarer can find"
                : SemanticAvailable() ? "No declaration found at the caret"
                : "Declarations are found once the folder is trusted and its project read";
        }
    }

    /// <summary>What Ctrl+click reaches at an offset: an address there, else the name there where a running server declares
    /// it; null for neither. Starts no server.</summary>
    private async Task<TextRange?> ReachAsync(int offset, CancellationToken cancel)
    {
        if (Editor.LinkOn(offset) is { } link)
        {
            return link.Range;
        }
        if (SourcePath is not { } path || Editor.TokenAt(offset) is not { } token)
        {
            return null;
        }
        TextPosition at = Editor.Document.Snapshot.GetPosition(offset);
        return await DeclarationAsync(path, at, startServer: false, cancel) is not null ? token : null;
    }

    /// <summary>Where the server of a file's language says what is at a place of it is declared, in a file on disk (a
    /// library's class inside a jar has none to open); null where it declares nothing, or no server can answer: one started
    /// for the ask only where <paramref name="startServer"/>.</summary>
    private async Task<(string Path, TextPosition At)?> DeclarationAsync(string path, TextPosition at, bool startServer, CancellationToken cancel)
    {
        if (DeclarationFinder is { } finder)
        {
            return await finder(path, at, cancel);
        }
        if (Language(path) is null || (startServer ? !SemanticAvailable() : RunningServerFor(path) is null))
        {
            return null;
        }
        try
        {
            JvmLanguageClient client = await LanguageClientAsync(path, cancel);
            JsonElement answer = await client.RequestAsync("textDocument/definition", new { textDocument = DocumentId(path), position = Point(at) }, cancel);
            return FirstDeclaration(answer);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The first place a definition answer names in a file on disk: a Location, a list of them, or LocationLinks.</summary>
    public static (string Path, TextPosition At)? FirstDeclaration(JsonElement answer)
    {
        List<JsonElement> places = answer.ValueKind switch
        {
            JsonValueKind.Array => [.. answer.EnumerateArray()],
            JsonValueKind.Object => [answer],
            _ => [],
        };
        foreach (JsonElement place in places)
        {
            string? uri = place.TryGetProperty("targetUri", out JsonElement target) ? target.GetString()
                : place.TryGetProperty("uri", out JsonElement plain) ? plain.GetString() : null;
            JsonElement range = place.TryGetProperty("targetSelectionRange", out JsonElement named) ? named
                : place.TryGetProperty("range", out JsonElement whole) ? whole : default;
            if (uri is null || range.ValueKind != JsonValueKind.Object || !Uri.TryCreate(uri, UriKind.Absolute, out Uri? file) || !file.IsFile)
            {
                continue;
            }
            return (file.LocalPath, PositionOf(range.GetProperty("start")));
        }
        return null;
    }

    /// <summary>Opens what a link written in a file names: a web address in the browser, a file in the editor, a folder in
    /// the file manager; one gone is said in the status line.</summary>
    private void FollowLink(object target)
    {
        switch (target)
        {
            case WebLink web:
                browsed.Add(web.Address);
                IdeErrors.Observe(Launcher.LaunchUriAsync(web.Address));
                StatusText = $"Opened {web.Address.AbsoluteUri} in the browser";
                break;
            case WrittenFileLink { Path: var file } when File.Exists(file):
                GoToFile(file, null);
                break;
            case WrittenFileLink { Path: var folder } when Directory.Exists(folder):
                FileManager.Show(this, folder);
                break;
            case WrittenFileLink { Path: var gone }:
                StatusText = $"{gone} is not there";
                break;
        }
    }
}
