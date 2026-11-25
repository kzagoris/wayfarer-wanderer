using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Editor;
using WayfarerWanderer.App.Shell;
using SyntaxLanguage = WayfarerWanderer.App.Shell.Languages.Language;
using TextRun = Avalonia.Controls.Documents.Run;

namespace WayfarerWanderer.App;

/// <summary>An IntelliJ IDEA-style Quick Documentation popup card over the editor: declaring owner with package icon,
/// syntax-coloured signature (wrapping parameters onto indented lines when long), hairline divider, scrollable rich
/// documentation body (paragraphs, inline code chips, bold/italic, clickable links, structured Params/Returns/Throws
/// sections, bullet lists, and fenced code blocks), and a footer bar with the JDK/module/source name plus Copy and
/// Go to Declaration actions. Interactive on hover so the pointer can enter to scroll, select text or click links; sticky
/// when opened via Quick Documentation (<c>Ctrl+Q</c>) until Escape or a click outside.</summary>
public sealed class QuickDocCard
{
    private const double MinCardWidth = 260;
    private const double MaxCardHeight = 380;
    private const int WrapSignatureThreshold = 36;
    private const int InlinePillMaxLength = 42;

    private static Cursor? handCursor;

    private readonly CodeEditor editor;
    private readonly Border frame;
    private readonly DockPanel layout = new();
    private readonly ScrollViewer scroller;
    private readonly StackPanel content = new() { Spacing = 0 };
    private readonly Border footerBar;
    private TopLevel? watched;
    private QuickDoc? current;
    private int closeGeneration;

    public QuickDocCard(CodeEditor editor)
    {
        this.editor = editor;
        scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = MaxCardHeight,
            Content = content,
        };
        footerBar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Gaps.Wide, Gaps.Small),
            IsVisible = false,
            [DockPanel.DockProperty] = Dock.Bottom,
        };
        footerBar.Bind(Border.BorderBrushProperty, footerBar.GetResourceObservable(TokenKeys.PopupBorder));
        layout.Children.Add(footerBar);
        layout.Children.Add(scroller);

        frame = new Border
        {
            Classes = { StyleClasses.Popup },
            MinWidth = MinCardWidth,
            MaxWidth = PopupFrame.Medium,
            Padding = new Thickness(0),
            Child = layout,
            IsHitTestVisible = true,
            IsVisible = false,
            ClipToBounds = false,
        };
        AutomationProperties.SetName(frame, "Quick Documentation");

        frame.PointerEntered += (_, _) => closeGeneration++;
        frame.PointerExited += (_, _) =>
        {
            if (!IsSticky && !frame.IsKeyboardFocusWithin)
            {
                ScheduleClose(TimeSpan.FromMilliseconds(220));
            }
        };
        frame.AddHandler(InputElement.PointerPressedEvent, (_, _) => closeGeneration++, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Raised when the user clicks an actionable web or file link inside the card.</summary>
    public event Action<Uri>? LinkClicked;

    /// <summary>Raised when the user clicks the Go to Declaration icon in the card's footer.</summary>
    public event Action? GoToDeclarationRequested;

    public bool IsOpen => frame.IsVisible && frame.Parent is not null;

    /// <summary>Whether the card stays open until Escape or a click outside (opened by Quick Documentation).</summary>
    public bool IsSticky { get; private set; }

    public bool IsPointerOver => IsOpen && frame.IsPointerOver;

    public bool IsFocusWithin => IsOpen && frame.IsKeyboardFocusWithin;

    /// <summary>The structured document currently displayed, or null when closed: for tests.</summary>
    public QuickDoc? Document => IsOpen ? current : null;

    /// <summary>Plain-text content of the open card, or empty when closed: for tests and clipboard copy.</summary>
    public string Text => IsOpen && current is not null ? current.PlainText() : string.Empty;

    /// <summary>The footer label currently shown, or empty when no footer is shown: for tests.</summary>
    public string FooterText { get; private set; } = string.Empty;

    /// <summary>The card's visual frame: for tests and layout inspection.</summary>
    public Border Frame => frame;

    /// <summary>Shows a structured Quick Documentation card anchored to <paramref name="below"/> (under the line whose top
    /// is <paramref name="lineTop"/>), flipping above the line when there is insufficient room below.</summary>
    public void Show(QuickDoc doc, Point below, double lineTop, string? footer = null, bool sticky = false, bool canGoToDeclaration = false)
    {
        if (doc.IsEmpty)
        {
            Close();
            return;
        }
        Close();
        current = doc;
        IsSticky = sticky;
        closeGeneration++;

        Size room = TopLevel.GetTopLevel(editor)?.ClientSize ?? OverlayLayer.GetOverlayLayer(editor)?.Bounds.Size ?? new Size(800, 600);
        double maxWidth = Math.Clamp(room.Width - (Gaps.Group * 2), MinCardWidth, PopupFrame.Medium);
        frame.MaxWidth = maxWidth;
        scroller.MaxHeight = Math.Clamp(room.Height * 0.58, 180, MaxCardHeight);

        BuildContent(doc, maxWidth);
        BuildFooter(doc, footer, canGoToDeclaration);

        if (content.Children.Count == 0 || OverlayLayer.GetOverlayLayer(editor) is not { } layer
            || editor.TranslatePoint(below, layer) is not { } place
            || editor.TranslatePoint(new Point(below.X, lineTop), layer) is not { } top)
        {
            Close();
            return;
        }

        if (frame.Parent != layer)
        {
            (frame.Parent as Panel)?.Children.Remove(frame);
            layer.Children.Add(frame);
        }
        Watch();
        scroller.Offset = default;
        frame.IsVisible = true;
        frame.Measure(new Size(maxWidth, Math.Max(120, room.Height - Gaps.Group)));
        Size size = frame.DesiredSize;
        double left = Math.Max(Gaps.Normal, Math.Min(place.X, room.Width - size.Width - Gaps.Normal));
        double belowRoom = room.Height - place.Y - Gaps.Normal;
        double aboveRoom = top.Y - Gaps.Normal;
        double y = size.Height <= belowRoom || belowRoom >= aboveRoom
            ? Math.Max(Gaps.Normal, Math.Min(place.Y, room.Height - size.Height - Gaps.Normal))
            : Math.Max(Gaps.Normal, top.Y - size.Height - Gaps.Tight);
        Canvas.SetLeft(frame, left);
        Canvas.SetTop(frame, y);
    }

    /// <summary>Promotes an open hover card to sticky (e.g., pressing Quick Documentation while already hovered).</summary>
    public void MakeSticky()
    {
        if (!IsOpen)
        {
            return;
        }
        IsSticky = true;
        closeGeneration++;
        scroller.Focus();
    }

    /// <summary>Schedules closing the card after <paramref name="delay"/> unless it is sticky, hovered, or focused.</summary>
    public void ScheduleClose(TimeSpan delay)
    {
        if (!IsOpen || IsSticky)
        {
            return;
        }
        int generation = ++closeGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == closeGeneration && IsOpen && !IsSticky && !frame.IsPointerOver && !frame.IsKeyboardFocusWithin)
            {
                Close();
            }
        }, delay);
    }

    public void Close()
    {
        closeGeneration++;
        bool returnFocus = frame.IsKeyboardFocusWithin;
        frame.IsVisible = false;
        content.Children.Clear();
        footerBar.Child = null;
        footerBar.IsVisible = false;
        FooterText = string.Empty;
        current = null;
        IsSticky = false;
        (frame.Parent as Panel)?.Children.Remove(frame);
        if (returnFocus && editor.IsVisible)
        {
            editor.Focus();
        }
    }

    private void BuildContent(QuickDoc doc, double maxWidth)
    {
        content.Children.Clear();
        bool hasHeader = !string.IsNullOrWhiteSpace(doc.Owner) || !string.IsNullOrWhiteSpace(doc.Signature);
        bool hasBody = doc.Body.Count > 0;

        if (hasHeader)
        {
            StackPanel header = new()
            {
                Spacing = Gaps.Small,
                Margin = new Thickness(Gaps.Wide, Gaps.Normal, Gaps.Wide, Gaps.Normal),
            };
            if (!string.IsNullOrWhiteSpace(doc.Owner))
            {
                header.Children.Add(BuildOwnerRow(doc.Owner));
            }
            if (!string.IsNullOrWhiteSpace(doc.Signature))
            {
                header.Children.Add(BuildSignature(doc.Signature, doc.SignatureLanguage));
            }
            content.Children.Add(header);
        }

        if (hasHeader && hasBody)
        {
            content.Children.Add(Divider(inset: true));
        }

        if (hasBody)
        {
            StackPanel body = new()
            {
                Spacing = Gaps.Normal,
                Margin = new Thickness(Gaps.Wide, Gaps.Normal, Gaps.Wide, Gaps.Normal),
            };
            foreach (QuickDocBlock block in doc.Body)
            {
                if (BuildBlock(block) is { } control)
                {
                    body.Children.Add(control);
                }
            }
            if (body.Children.Count > 0)
            {
                content.Children.Add(body);
            }
        }
    }

    private Control BuildOwnerRow(string owner)
    {
        PathIcon icon = new()
        {
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(IconKeys.Package));
        icon.Bind(TemplatedControl.ForegroundProperty, icon.GetResourceObservable(TokenKeys.Link));

        SelectableTextBlock name = new()
        {
            Text = owner,
            FontFamily = editor.FontFamily,
            FontSize = Math.Max(11.5, editor.FontSize - 0.5),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Bind(TextElement.ForegroundProperty, name.GetResourceObservable(TokenKeys.Link));

        return new DockPanel
        {
            HorizontalSpacing = Gaps.Small,
            Children =
            {
                icon,
                name,
            },
        };
    }

    private Control BuildSignature(string signature, string languageInfo)
    {
        SyntaxLanguage? language = QuickDocSyntax.LanguageOf(languageInfo);
        IReadOnlyList<SyntaxSpan> rawSpans = QuickDocSyntax.Tokenize(signature, language);

        string shown = signature;
        IReadOnlyList<SyntaxSpan> spans = rawSpans;
        if (ShouldBreakParameters(signature) && QuickDocSyntax.BreakParameters(signature) is { } formatted)
        {
            shown = formatted.Text;
            spans = formatted.Map(rawSpans);
        }

        SelectableTextBlock block = BuildHighlightedCodeBlock(shown, spans, language);
        block.LineHeight = Math.Round(editor.FontSize * 1.45);
        return block;
    }

    private static bool ShouldBreakParameters(string signature)
    {
        if (signature.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }
        int open = signature.IndexOf('(');
        int close = signature.LastIndexOf(')');
        if (open < 0 || close <= open + 1)
        {
            return false;
        }
        // Format parameter lists on separate indented lines when long enough or multi-parameter, matching IDEA.
        return signature.Length >= WrapSignatureThreshold || signature.AsSpan(open + 1, close - open - 1).Contains(',');
    }

    private SelectableTextBlock BuildHighlightedCodeBlock(string code, IReadOnlyList<SyntaxSpan> spans, SyntaxLanguage? language)
    {
        SelectableTextBlock block = new()
        {
            FontFamily = editor.FontFamily,
            FontSize = editor.FontSize,
            TextWrapping = TextWrapping.Wrap,
            Inlines = [],
        };
        block.Bind(TextElement.ForegroundProperty, block.GetResourceObservable(TokenKeys.Editor_Text));
        if (language is not null)
        {
            ThemeLanguages.Follow(block, language.Id);
        }

        int at = 0;
        foreach (SyntaxSpan span in spans)
        {
            if (span.Start < at || span.End > code.Length)
            {
                continue;
            }
            if (span.Start > at)
            {
                block.Inlines.Add(new TextRun(code[at..span.Start]));
            }
            TextRun run = new(code[span.Start..span.End]);
            if (span.Kind != TokenKind.Text)
            {
                ApplyTokenLook(block, run, span.Kind);
            }
            block.Inlines.Add(run);
            at = span.End;
        }
        if (at < code.Length)
        {
            block.Inlines.Add(new TextRun(code[at..]));
        }
        return block;
    }

    private static void ApplyTokenLook(Control host, TextRun run, TokenKind kind)
    {
        run.Bind(TextElement.ForegroundProperty, host.GetResourceObservable($"Passenger.Syntax.{kind}"));
        string styleKey = $"Passenger.SyntaxStyle.{kind}";
        run.Bind(TextElement.FontWeightProperty, host.GetResourceObservable(styleKey, set =>
            (set is string named ? named.Contains("Bold", StringComparison.Ordinal) : kind is TokenKind.Heading or TokenKind.Bold)
                ? FontWeight.Bold
                : FontWeight.Normal));
        run.Bind(TextElement.FontStyleProperty, host.GetResourceObservable(styleKey, set =>
            (set is string named ? named.Contains("Italic", StringComparison.Ordinal) : kind is TokenKind.UserInput or TokenKind.Italic)
                ? FontStyle.Italic
                : FontStyle.Normal));
    }

    private Control? BuildBlock(QuickDocBlock block) => block switch
    {
        QuickDocParagraph paragraph => BuildRichText(paragraph.Inlines, paragraph.HeadingLevel),
        QuickDocCodeBlock code => BuildFencedCodeBlock(code),
        QuickDocList list => BuildList(list),
        QuickDocTagSection section => BuildTagSection(section),
        QuickDocDivider => Divider(inset: false),
        _ => null,
    };

    private Control BuildFencedCodeBlock(QuickDocCodeBlock block)
    {
        SyntaxLanguage? language = QuickDocSyntax.LanguageOf(block.Language);
        IReadOnlyList<SyntaxSpan> spans = QuickDocSyntax.Tokenize(block.Code, language);
        SelectableTextBlock code = BuildHighlightedCodeBlock(block.Code, spans, language);
        code.FontSize = Math.Max(11.5, editor.FontSize - 0.5);
        Border container = new()
        {
            Classes = { StyleClasses.Code },
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(Gaps.Normal, Gaps.Small),
            Child = code,
        };
        container.Bind(Border.BackgroundProperty, container.GetResourceObservable(TokenKeys.Field));
        return container;
    }

    private Control BuildList(QuickDocList list)
    {
        StackPanel stack = new() { Spacing = Gaps.Tight };
        for (int i = 0; i < list.Items.Count; i++)
        {
            TextBlock bullet = new()
            {
                Text = list.Ordered ? $"{list.Start + i}." : "•",
                MinWidth = 16,
                TextAlignment = list.Ordered ? TextAlignment.Right : TextAlignment.Center,
                Margin = new Thickness(0, 0, Gaps.Small, 0),
                LineHeight = 20,
            };
            bullet.Bind(TextElement.ForegroundProperty, bullet.GetResourceObservable(TokenKeys.TextMuted));

            Control itemText = BuildRichText(list.Items[i]);
            Grid.SetColumn(itemText, 1);
            stack.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children = { bullet, itemText },
            });
        }
        return stack;
    }

    private Control BuildTagSection(QuickDocTagSection section)
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = Gaps.Normal,
        };

        bool hasLeadPill = section.Items.Count > 0 && !string.IsNullOrWhiteSpace(section.Items[0].Name);
        TextBlock label = new()
        {
            Text = section.Label,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, hasLeadPill ? 3 : 0, 0, 0),
        };
        label.Bind(TextElement.ForegroundProperty, label.GetResourceObservable(TokenKeys.TextMuted));
        grid.Children.Add(label);

        StackPanel items = new() { Spacing = Gaps.Tight };
        Grid.SetColumn(items, 1);
        foreach (QuickDocTagItem item in section.Items)
        {
            List<QuickDocInline> composed = [];
            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                composed.Add(new QuickDocCodeSpan(item.Name));
                if (item.Description.Count > 0)
                {
                    composed.Add(new QuickDocText(" – "));
                }
            }
            composed.AddRange(item.Description);
            items.Children.Add(BuildRichText(composed));
        }
        grid.Children.Add(items);
        return grid;
    }

    private Control BuildRichText(IReadOnlyList<QuickDocInline> inlines, int headingLevel = 0)
    {
        RichDocText block = new(editor.FontFamily, editor.FontSize, target => LinkClicked?.Invoke(target))
        {
            TextWrapping = TextWrapping.Wrap,
            LineSpacing = 4,
        };
        block.Bind(TextElement.ForegroundProperty, block.GetResourceObservable(TokenKeys.Text));
        if (headingLevel > 0)
        {
            block.FontWeight = FontWeight.SemiBold;
            if (headingLevel <= 2)
            {
                block.FontSize = 14;
            }
        }
        block.Append(inlines);
        return block;
    }

    private void BuildFooter(QuickDoc doc, string? contextFooter, bool canGoToDeclaration)
    {
        string? label = !string.IsNullOrWhiteSpace(doc.SourceLabel) ? doc.SourceLabel : contextFooter;
        Uri? sourceUri = !string.IsNullOrWhiteSpace(doc.SourceTarget) ? TryActionableUri(doc.SourceTarget) : null;
        FooterText = label ?? string.Empty;

        DockPanel row = new() { HorizontalSpacing = Gaps.Small };

        // Right-aligned compact action buttons (Go to Declaration + Copy), matching IDEA's bottom-right controls.
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Hair,
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };

        if (canGoToDeclaration)
        {
            Button go = ToolButtons.Of(IconKeys.DocumentText, "Go to Declaration", () =>
            {
                Close();
                GoToDeclarationRequested?.Invoke();
            });
            go.Classes.Add(StyleClasses.Small);
            actions.Children.Add(go);
        }

        Button copy = ToolButtons.Of(IconKeys.Copy, "Copy Documentation", CopyToClipboard);
        copy.Classes.Add(StyleClasses.Small);
        actions.Children.Add(copy);
        row.Children.Add(actions);

        if (!string.IsNullOrWhiteSpace(label))
        {
            PathIcon icon = new()
            {
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(IconKeys.Explorer));
            icon.Bind(TemplatedControl.ForegroundProperty, icon.GetResourceObservable(TokenKeys.Link));

            Control labelControl;
            if (sourceUri is not null)
            {
                Button link = new()
                {
                    Classes = { StyleClasses.Link, StyleClasses.Caption },
                    Content = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0),
                    MinHeight = 0,
                };
                link.Click += (_, _) => LinkClicked?.Invoke(sourceUri);
                labelControl = link;
            }
            else
            {
                TextBlock caption = new()
                {
                    Classes = { StyleClasses.Caption },
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                caption.Bind(TextElement.ForegroundProperty, caption.GetResourceObservable(TokenKeys.TextMuted));
                labelControl = caption;
            }

            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gaps.Small,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, labelControl },
            });
        }

        footerBar.Child = row;
        footerBar.IsVisible = true;
    }

    private void CopyToClipboard()
    {
        if (Text is { Length: > 0 } text)
        {
            IdeErrors.Observe(Shell.ToolWindows.ListMenus.CopyAsync(editor, text));
        }
    }

    private static Border Divider(bool inset)
    {
        Border rule = new()
        {
            Height = 1,
            Margin = inset ? new Thickness(Gaps.Wide, 0) : new Thickness(0, Gaps.Hair),
        };
        rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(TokenKeys.PopupBorder));
        return rule;
    }

    private static Uri? TryActionableUri(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme is "http" or "https" || uri.IsFile)
            ? uri
            : null;

    /// <summary>Dismisses the card on Escape or any click outside the card.</summary>
    private void Watch()
    {
        if (watched is not null || TopLevel.GetTopLevel(editor) is not { } top)
        {
            return;
        }
        watched = top;
        top.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!IsOpen || frame.IsPointerOver)
            {
                return;
            }
            if (e.Source is Visual source && (source == frame || source.GetVisualAncestors().Any(a => a == frame || a is MenuItem or ContextMenu or PopupRoot)))
            {
                return;
            }
            Close();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && IsOpen)
            {
                Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Selectable rich-text paragraph supporting bold, italic, inline code pills and clickable links.</summary>
    private sealed class RichDocText : SelectableTextBlock
    {
        private readonly FontFamily codeFont;
        private readonly double codeSize;
        private readonly Action<Uri> openLink;
        private readonly List<(int Start, int End, Uri Target)> links = [];
        private readonly StringBuilder shown = new();

        public RichDocText(FontFamily codeFont, double codeSize, Action<Uri> openLink)
        {
            this.codeFont = codeFont;
            this.codeSize = Math.Max(11.5, codeSize - 1);
            this.openLink = openLink;
            Inlines ??= [];
        }

        public void Append(IReadOnlyList<QuickDocInline> parts, bool bold = false, bool italic = false, Uri? link = null)
        {
            foreach (QuickDocInline part in parts)
            {
                switch (part)
                {
                    case QuickDocText text:
                        PutRun(text.Value, bold, italic, link, code: false);
                        break;
                    case QuickDocCodeSpan code:
                        PutCode(code.Value, bold, italic, link);
                        break;
                    case QuickDocBold strong:
                        Append(strong.Parts, bold: true, italic, link);
                        break;
                    case QuickDocItalic emphasis:
                        Append(emphasis.Parts, bold, italic: true, link);
                        break;
                    case QuickDocLink linked:
                        Uri? uri = TryActionableUri(linked.Target);
                        int start = shown.Length;
                        Append(linked.Parts, bold, italic, uri ?? link);
                        if (uri is not null && shown.Length > start)
                        {
                            links.Add((start, shown.Length, uri));
                        }
                        break;
                    case QuickDocBreak:
                        Inlines!.Add(new LineBreak());
                        shown.Append('\n');
                        break;
                }
            }
        }

        private void PutCode(string code, bool bold, bool italic, Uri? link)
        {
            // Short inline code tokens render as IDEA-style rounded pills; longer spans stay wrapping runs.
            if (code.Length <= InlinePillMaxLength && !code.Contains('\n') && link is null)
            {
                TextBlock label = new()
                {
                    Text = code,
                    FontFamily = codeFont,
                    FontSize = codeSize,
                    FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                    FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                label.Bind(TextElement.ForegroundProperty, label.GetResourceObservable(TokenKeys.Text));
                Border pill = new()
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(1, 0, 1, -2),
                    Child = label,
                };
                pill.Bind(Border.BackgroundProperty, pill.GetResourceObservable(TokenKeys.Field));
                Inlines!.Add(new InlineUIContainer(pill) { BaselineAlignment = BaselineAlignment.TextBottom });
                shown.Append('\uFFFC');
                return;
            }
            PutRun(code, bold, italic, link, code: true);
        }

        private void PutRun(string text, bool bold, bool italic, Uri? link, bool code)
        {
            TextRun run = new(text);
            if (bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }
            if (italic)
            {
                run.FontStyle = FontStyle.Italic;
            }
            if (code)
            {
                run.FontFamily = codeFont;
                run.FontSize = codeSize;
                run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable(TokenKeys.Field));
            }
            if (link is not null)
            {
                run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable(TokenKeys.Link));
            }
            Inlines!.Add(run);
            shown.Append(text);
        }

        private Uri? LinkAt(Point point)
        {
            if (links.Count == 0)
            {
                return null;
            }
            int at = TextLayout.HitTestPoint(point).TextPosition;
            (int start, int end, Uri? target) = links.FirstOrDefault(l => at >= l.Start && at < l.End);
            return target is not null && TextLayout.HitTestTextRange(start, end - start).Any(rect => rect.Contains(point))
                ? target
                : null;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (LinkAt(e.GetPosition(this)) is null)
            {
                ClearValue(CursorProperty);
            }
            else
            {
                Cursor = handCursor ??= new Cursor(StandardCursorType.Hand);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton == MouseButton.Left && SelectionStart == SelectionEnd && LinkAt(e.GetPosition(this)) is { } target)
            {
                e.Handled = true;
                openLink(target);
            }
        }
    }
}
