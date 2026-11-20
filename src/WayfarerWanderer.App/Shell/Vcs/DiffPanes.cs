// From Passenger 2044e49b src/Passenger.App/Vcs/DiffPanes.cs, MIT (notices/Passenger/LICENSE): each pane coloured by a
// highlighter of its own.
using Avalonia.Controls;
using WayfarerWanderer.App.Shell.Workspace;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>Every diff and merge a window shows, made one way whoever opens it: each pane coloured by its file's language;
/// a diff's Highlighting Differences as Settings
/// has it, a change to it kept for every diff after; and the colours made let go once the view leaves its window.</summary>
/// <param name="chosen">Highlighting Differences as Settings has it now.</param>
/// <param name="keep">Keeps the way a person chose in a diff.</param>
public sealed class DiffPanes(Func<DiffHighlighting> chosen, Action<DiffHighlighting> keep)
{
    /// <summary>A file's diff: <paramref name="diff"/>'s other text on the left in <paramref name="leftPath"/>'s colours,
    /// <paramref name="mine"/> on the right in <paramref name="rightPath"/>'s, editable while it is <paramref name="open"/>'s
    /// own text.</summary>
    public DiffView ForFile(string name, FileDiff diff, TextDocument mine, OpenDocument? open, (string Left, string Right) titles,
        string leftPath, string rightPath)
    {
        DiffView view = new(name, diff, mine, editable: open is not null, titles, chosen()) { Source = rightPath };
        view.HighlightingChanged += (_, way) => keep(way);
        Colour(view, [(view.Left, leftPath, null), (view.Right, rightPath, open)]);
        return view;
    }

    /// <summary>A file's three-way merge, its three panes in the file's colours.</summary>
    public static MergeView ForMerge(MergeTexts texts, string path)
    {
        MergeView view = new(texts, Path.GetFileName(path));
        Colour(view, [(view.Left, path, null), (view.Result, path, null), (view.Right, path, null)]);
        return view;
    }

    /// <summary>Colours each pane while the view is in a window: let go as it leaves one, made again as it is put back
    /// (after the view shows its texts again, whose handler runs first).</summary>
    private static void Colour(Control view, IReadOnlyList<(CodeEditor Pane, string Path, OpenDocument? Open)> panes)
    {
        List<Action> letGo = [];
        bool coloured = false;
        void Paint()
        {
            coloured = true;
            foreach ((CodeEditor pane, string path, OpenDocument? open) in panes)
            {
                ColourPane(pane, path, open, letGo);
            }
        }
        Paint();
        view.DetachedFromVisualTree += (_, _) =>
        {
            coloured = false;
            letGo.ForEach(go => go());
            letGo.Clear();
        };
        view.AttachedToVisualTree += (_, _) =>
        {
            if (!coloured)
            {
                Paint();
            }
        };
    }

    /// <summary>A pane's colours: a highlighter of its own for the file's language, let go by <paramref name="letGo"/>, the
    /// theme's for the language over them. An open file's own text is coloured afresh, as Wayfarer's languages colour one
    /// text per highlighter.</summary>
    private static void ColourPane(CodeEditor pane, string path, OpenDocument? open, List<Action> letGo)
    {
        Languages.Language language = Languages.Language.ForPath(open?.Path ?? path);
        ThemeLanguages.Follow(pane, language.Id);
        if (Languages.Highlighters.For(language, pane.Document) is { } colours)
        {
            pane.Highlighter = colours;
            if (colours is IDisposable owned)
            {
                letGo.Add(owned.Dispose);
            }
        }
    }
}
