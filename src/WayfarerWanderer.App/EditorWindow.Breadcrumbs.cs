// From Passenger 2044e49b src/Passenger.App/MainWindow.Breadcrumbs.cs, MIT (notices/Passenger/LICENSE): the outline asked of
// the JVM language servers.
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Navigation;

namespace WayfarerWanderer.App;

/// <summary>The status bar's path: the shown file's last folders and its name, then the type and member the caret is in,
/// from the language server's outline (asked again as editing pauses, and never starting a server of its own accord); a
/// click on a member goes to it, on the file or a folder shows it in the Project window.</summary>
public sealed partial class EditorWindow
{
    private readonly PathCrumbs breadcrumbs = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 18, 0) };
    private IReadOnlyList<SymbolRow> outline = [];
    private string? outlined;
    private Debounce? outlineAgain;
    private int outlining;
    private TextDocument? followedText;

    /// <summary>What the path shows, so a caret that moves within the same member leaves its buttons as they are.</summary>
    private readonly List<Crumb> shownCrumbs = [];

    /// <summary>The path's buttons and the marks between them, made as the longest path shown needs them; those past the path
    /// shown are hidden.</summary>
    private readonly List<(TextBlock Between, Button Crumb)> crumbButtons = [];

    /// <summary>The path shown, part by part.</summary>
    public IReadOnlyList<string> Breadcrumbs => [.. shownCrumbs.Select(c => c.Target is null ? c.Text : NameOf(c.Text))];

    /// <summary>How long editing pauses before the outline is asked for again.</summary>
    private static readonly TimeSpan OutlinePause = TimeSpan.FromMilliseconds(400);

    private void AddBreadcrumbs()
    {
        breadcrumbHost.Children.Add(breadcrumbs);
        outlineAgain = new Debounce(OutlinePause, OutlineAsync);
    }

    /// <summary>Another file shown, or none: its own parts at once, and its outline asked for.</summary>
    private void BreadcrumbsFollowFile()
    {
        TextDocument? text = Editor.IsVisible && SourcePath is not null ? Editor.Document : null;
        if (!ReferenceEquals(text, followedText))
        {
            if (followedText is not null)
            {
                followedText.Changed -= OnFollowedTextChanged;
            }
            followedText = text;
            if (text is not null)
            {
                text.Changed += OnFollowedTextChanged;
            }
            outline = [];
            outlined = null;
            OutlineLater();
        }
        ShowBreadcrumbs();
    }

    private void OnFollowedTextChanged(object? sender, DocumentChangedEventArgs e) => OutlineLater();

    /// <summary>The running server of a file's language, or null: the path asks only a server already up.</summary>
    private Jvm.JvmLanguageClient? RunningServerFor(string path) =>
        Session is { IsTrusted: true } session && PathNames.IsUnder(path, session.Root) && Language(path) is { } language
            && languageClients.TryGetValue(language, out Jvm.JvmLanguageClient? client) && !client.HasExited ? client : null;

    /// <summary>Asks for the outline once editing pauses, for a file a running server has.</summary>
    private void OutlineLater()
    {
        if (SourcePath is { } path && RunningServerFor(path) is not null)
        {
            outlineAgain?.Restart();
        }
    }

    /// <summary>Asks for the outline again as a server comes up or says it is ready while the path has none for the file
    /// shown: asked before, it found none.</summary>
    private void OutlineWhenUnseen()
    {
        if (SourcePath is { } path && outlined != path && RunningServerFor(path) is not null)
        {
            IdeErrors.Observe(OutlineAsync());
        }
    }

    private async Task OutlineAsync()
    {
        int asked = ++outlining;
        if (SourcePath is not { } path || RunningServerFor(path) is null)
        {
            return;
        }
        try
        {
            Jvm.JvmLanguageClient client = await LanguageClientAsync(path, semanticLifetime.Token);
            JsonElement answer = await client.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri = new Uri(path).AbsoluteUri } }, semanticLifetime.Token);
            if (asked == outlining && SourcePath == path)
            {
                outline = DocumentSymbols(answer, path);
                outlined = path;
                ShowBreadcrumbs();
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            // The path keeps the file's own parts; the next edit asks again.
        }
    }

    private void ShowBreadcrumbs()
    {
        List<Crumb> wanted = [];
        if (Editor.IsVisible && SourcePath is { } path)
        {
            wanted.AddRange(FileCrumbs(path));
            if (outlined == path)
            {
                int line = Editor.CaretPosition.Line;
                // The rows are in the file's order, each under its container: the caret's chain is the deepest run of rows
                // spanning its line, each a level deeper than the one before.
                List<SymbolRow> chain = [];
                foreach (SymbolRow row in outline)
                {
                    if (!row.Spans(line))
                    {
                        continue;
                    }
                    while (chain.Count > 0 && chain[^1].Depth >= row.Depth)
                    {
                        chain.RemoveAt(chain.Count - 1);
                    }
                    if (row.Depth == chain.Count)
                    {
                        chain.Add(row);
                    }
                }
                // The package is the file's own context; the path names what is in it.
                wanted.AddRange(chain.Where(r => r.Kind is not (SymbolKind.Package or SymbolKind.Namespace or SymbolKind.Module))
                    .Select(r => new Crumb(r.Name, path, r.Position, Muted: false)));
            }
        }
        if (wanted.SequenceEqual(shownCrumbs))
        {
            return;
        }
        shownCrumbs.Clear();
        shownCrumbs.AddRange(wanted);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i == crumbButtons.Count)
            {
                AddCrumbButton();
            }
            (TextBlock between, Button crumb) = crumbButtons[i];
            Crumb part = wanted[i];
            between.IsVisible = i > 0;
            crumb.IsVisible = true;
            crumb.Content = part.Target is null ? part.Text : NameOf(part.Text);
            ListRow.Dim(crumb, part.Muted);
        }
        for (int i = wanted.Count; i < crumbButtons.Count; i++)
        {
            crumbButtons[i].Between.IsVisible = false;
            crumbButtons[i].Crumb.IsVisible = false;
        }
    }

    /// <summary>The file's parts of the path: its last two folders under the open folder (a deep path would push the rest of
    /// the status bar off) and its name.</summary>
    private Crumb[] FileCrumbs(string path)
    {
        string? root = Session?.Root;
        string relative = root is not null && PathNames.IsUnder(path, root) ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        List<Crumb> made = [];
        for (int i = Math.Max(0, parts.Length - 3); i < parts.Length - 1; i++)
        {
            made.Add(new Crumb(parts[i], Path.Combine([root ?? string.Empty, .. parts[..(i + 1)]]), null, Muted: true));
        }
        made.Add(new Crumb(parts[^1], path, null, Muted: false));
        return [.. made];
    }

    /// <summary>A member's name as the path shows it: without the parameters or type its outline name may give.</summary>
    private static string NameOf(string name) => name.IndexOfAny(['(', ':', '<']) is var cut and > 0 ? name[..cut].TrimEnd() : name;

    /// <summary>A button of the path and the mark before it, which a click on reads the part shown there when clicked.</summary>
    private void AddCrumbButton()
    {
        int index = crumbButtons.Count;
        TextBlock between = new() { Text = "›", Classes = { StyleClasses.Status }, Margin = new Avalonia.Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        Button crumb = new() { Classes = { StyleClasses.Crumb }, Focusable = false };
        crumb.Click += (_, _) =>
        {
            if (index >= shownCrumbs.Count)
            {
                return;
            }
            Crumb part = shownCrumbs[index];
            if (part.Target is { } target)
            {
                NavigateTo(part.Path, target);
            }
            else
            {
                ToolDock.Show(ToolWindowIds.Explorer);
                Explorer.Reveal(part.Path);
            }
        };
        breadcrumbs.Children.Add(between);
        breadcrumbs.Children.Add(crumb);
        crumbButtons.Add((between, crumb));
    }

    /// <summary>A part of the path: a folder (muted) or the file, which a click shows in the Project window, or what the caret
    /// is in, which it goes to, named as its outline names it.</summary>
    private readonly record struct Crumb(string Text, string Path, TextPosition? Target, bool Muted);
}
