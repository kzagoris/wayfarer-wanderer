// From Passenger 8a1df7e4 src/Passenger.App/PlainTextView.cs, MIT (notices/Passenger/LICENSE): it answers when it closes,
// as Wayfarer's popups do (IAnswers), and takes Escape itself.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Navigation;

namespace WayfarerWanderer.App.Shell;

/// <summary>What the thing at hand says, as plain text only to read (Read as Text): what the language says of the caret,
/// the last thing the status bar said, the terminal's last command and its output, a diff's changes. One view for every
/// source: an editor a screen reader reads line by line as it reads the code, named by what it shows, its text to copy
/// whole; Escape, or Close, gives the keys back to where they were.</summary>
public sealed class PlainTextView : UserControl, IAnswers<object?>
{
    /// <summary>The most the text's editor is tall, less in a window too short for it.</summary>
    private const double TextHeight = 420;

    /// <param name="title">What the text is, said over it and as the editor's name.</param>
    /// <param name="text">The text, its lines as they are read.</param>
    public PlainTextView(string title, string text)
    {
        Title = title;
        Width = PopupFrame.Widest;
        Editor = new CodeEditor
        {
            Document = new TextDocument(text),
            IsReadOnly = true,
            ReadOnlyWhy = Refusals.OnlyToRead,
            LineCommentPrefix = null,
            ShowLineNumbers = false,
            ShowErrorStripe = false,
            SoftWrap = true,
            FontSize = TypeRamp.Code,
            MaxHeight = TextHeight,
        };
        AutomationProperties.SetName(Editor, title);
        CopyButton copy = new("Copy All") { Text = text };
        Button shut = new() { Content = "Close" };
        shut.Click += (_, _) => Close();
        Content = new Border
        {
            Classes = { StyleClasses.Popup },
            Padding = PopupFrame.Inset,
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Classes = { StyleClasses.PaneTitle },
                        Text = title,
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 0, 0, Gaps.Normal),
                        [DockPanel.DockProperty] = Dock.Top,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = Gaps.Normal,
                        Margin = new Thickness(0, Gaps.Wide, 0, 0),
                        Children = { copy, shut },
                        [DockPanel.DockProperty] = Dock.Bottom,
                    },
                    new Border { Classes = { StyleClasses.Framed }, Child = Editor },
                },
            },
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        // Once shown: a popup's content joins the window as its layer is laid out.
        Dispatcher.UIThread.Post(() => Editor.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>Closed, with nothing to answer.</summary>
    public event EventHandler<object?>? Done;

    /// <summary>What the text is.</summary>
    public string Title { get; }

    /// <summary>The text's editor, which has the keys as the view opens.</summary>
    public CodeEditor Editor { get; }

    /// <summary>The text shown.</summary>
    public string Text => Editor.Document.Snapshot.ToString();

    /// <summary>Closes the view, as Escape and Close do.</summary>
    public void Close() => Done?.Invoke(this, null);
}
