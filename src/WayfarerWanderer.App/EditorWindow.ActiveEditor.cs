// From Passenger 8a1df7e4 src/Passenger.App/MainWindow.ActiveEditor.cs and MainWindow.Settings.cs's ApplyTo, MIT
// (notices/Passenger/LICENSE): over Wayfarer's one editor, each file's soft wrap and whitespace kept with its view.
using Avalonia.Threading;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.App.Shell.Workspace;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.App;

/// <summary>The editor as Settings' Editor page has it: its font's size and line spacing, ligatures, line numbers, rainbow
/// braces, closing tags, wrapped fragments' indent and Ctrl+wheel zoom, followed as Settings previews or applies them. View,
/// Active Editor: Soft-Wrap and Show Whitespaces for the file shown, and Increase, Decrease and Reset Font Size, which
/// Ctrl+wheel (Cmd+wheel on macOS) also does while Settings allows it. A file opens soft-wrapped when Settings' masks match
/// its name and with whitespace shown when Settings shows it; the font size is Settings' own, so a zoom is kept as
/// Settings keeps it.</summary>
public sealed partial class EditorWindow
{
    private const string EditorGroup = "Editor";

    /// <summary>The settings the editor's look last followed.</summary>
    private AppSettings? editorLook;

    private void AddActiveEditor()
    {
        FollowEditorLook(SettingsKept.Effective);
        SettingsKept.PreviewChanged += EditorLookChanged;
        Themes.FontsChanged += EditorLookChanged;
        Closed += (_, _) =>
        {
            SettingsKept.PreviewChanged -= EditorLookChanged;
            Themes.FontsChanged -= EditorLookChanged;
        };
        Editor.ZoomRequested += (_, points) => Zoom(SettingsKept.Value.CodeSize() + points);

        Actions.Registry.Add(new IdeAction(A.SoftWrap, "Soft-Wrap", EditorGroup, _ =>
        {
            Shown view = ShownView!;
            view.SoftWrap = !view.SoftWrap;
            Editor.SoftWrap = view.SoftWrap;
        })
        {
            WhyNot = _ => ShownView is null ? Refusals.NoFile : null,
            IsChecked = _ => ShownView?.SoftWrap == true,
            Words = ["Wrap Lines", "Word Wrap"],
        });
        Actions.Registry.Add(new IdeAction(A.ShowWhitespaces, "Show Whitespaces", EditorGroup, _ =>
        {
            Shown view = ShownView!;
            view.ShowWhitespaces = !view.ShowWhitespaces;
            Editor.ShowWhitespaces = view.ShowWhitespaces;
        })
        {
            WhyNot = _ => ShownView is null ? Refusals.NoFile : null,
            IsChecked = _ => ShownView?.ShowWhitespaces == true,
            Words = ["Render Whitespace", "Invisible Characters"],
        });
        Actions.Registry.Add(new IdeAction(A.IncreaseFontSize, "Increase Font Size", EditorGroup, _ => Zoom(SettingsKept.Value.CodeSize() + 1))
        {
            WhyNot = _ => ShownView is null ? Refusals.NoFile
                : SettingsKept.Value.CodeSize() >= AppSettings.MaxFontSize ? "The editor's font is as large as it goes" : null,
            Words = ["Zoom In"],
        });
        Actions.Registry.Add(new IdeAction(A.DecreaseFontSize, "Decrease Font Size", EditorGroup, _ => Zoom(SettingsKept.Value.CodeSize() - 1))
        {
            WhyNot = _ => ShownView is null ? Refusals.NoFile
                : SettingsKept.Value.CodeSize() <= AppSettings.MinFontSize ? "The editor's font is as small as it goes" : null,
            Words = ["Zoom Out"],
        });
        // Back to the size the theme gives, or Wayfarer's own: set by nobody.
        Actions.Registry.Add(new IdeAction(A.ResetFontSize, "Reset Font Size", EditorGroup, _ => SettingsKept.Update(s => s with { EditorFontSize = null }))
        {
            WhyNot = _ => ShownView is null ? Refusals.NoFile : null,
            Words = ["Reset Zoom"],
        });
    }

    private void EditorLookChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => FollowEditorLook(SettingsKept.Effective));

    /// <summary>The editors' font at a size, within what Settings allows, kept as Settings keeps it.</summary>
    private void Zoom(double size)
    {
        double next = Math.Clamp(Math.Round(size), AppSettings.MinFontSize, AppSettings.MaxFontSize);
        if (next != SettingsKept.Value.CodeSize())
        {
            SettingsKept.Update(s => s with { EditorFontSize = next });
        }
    }

    /// <summary>The editor as the settings shown give it, and the open files' soft wrap and whitespace where those changed
    /// what Settings says of them.</summary>
    private void FollowEditorLook(AppSettings next)
    {
        AppSettings? before = editorLook;
        editorLook = next;
        Editor.FontSize = next.CodeSize();
        Editor.LineSpacing = next.EditorLineSpacing;
        Editor.Ligatures = next.Ligatures;
        Editor.ShowLineNumbers = next.ShowLineNumbers;
        Editor.RainbowBraces = next.RainbowBraces;
        Editor.ClosesTags = next.CloseTags;
        Editor.WrapIndent = next.SoftWrapIndent;
        Editor.ZoomsWithWheel = next.WheelZoom;
        if (before is not null)
        {
            FollowViewSettings(before, next);
        }
        Editor.InvalidateMeasure();
        Editor.InvalidateVisual();
    }

    /// <summary>The open files take a change to Settings' soft wrap or whitespace where it changes what Settings says of
    /// them, so a file turned on or off from View, Active Editor stays so otherwise.</summary>
    private void FollowViewSettings(AppSettings before, AppSettings next)
    {
        foreach ((string path, OpenDocument document) in documents)
        {
            if (shown.GetValueOrDefault(document.Document) is not { } view)
            {
                continue;
            }
            bool wraps = next.WrapsFile(path);
            if (wraps != before.WrapsFile(path))
            {
                view.SoftWrap = wraps;
            }
            if (next.ShowWhitespaces != before.ShowWhitespaces)
            {
                view.ShowWhitespaces = next.ShowWhitespaces;
            }
        }
        if (ShownView is { } current)
        {
            Editor.SoftWrap = current.SoftWrap;
            Editor.ShowWhitespaces = current.ShowWhitespaces;
        }
    }
}
