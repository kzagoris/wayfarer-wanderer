using System.Globalization;
using System.Text.Json;
using Avalonia;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>IntelliJ IDEA-style symbol hover and Quick Documentation (<c>Ctrl+Q</c>, <c>View &gt; Quick Documentation</c>):
/// resting the pointer on a symbol asks its running language server for <c>textDocument/hover</c> and opens
/// <see cref="QuickDocCard"/> under the line; pressing Quick Documentation asks at the caret (starting the server when the
/// workspace is trusted) and pins the card until Escape or a click outside. Yields to the debugger's value card while paused
/// and to the completion popup while completing.</summary>
public sealed partial class EditorWindow
{
    private QuickDocCard? quickDocCard;
    private CancellationTokenSource? quickDocAsk;
    private (string Path, TextPosition Position)? quickDocTarget;

    /// <summary>The Quick Documentation card over the editor: for tests.</summary>
    public QuickDocCard QuickDocCard => quickDocCard ??= NewQuickDocCard();

    /// <summary>Supplies hover Markdown in place of the language servers (or null for none): for tests.</summary>
    public Func<string, TextPosition, CancellationToken, Task<string?>>? HoverProvider { get; set; }

    private QuickDocCard NewQuickDocCard()
    {
        QuickDocCard card = new(Editor);
        card.LinkClicked += FollowQuickDocLink;
        card.GoToDeclarationRequested += () => IdeErrors.Observe(GoToQuickDocDeclarationAsync());
        return card;
    }

    private void AddQuickDocumentation()
    {
        Actions.Registry.Add(new IdeAction(AppActions.QuickDocumentation, "Quick Documentation", "View", _ => IdeErrors.Observe(QuickDocumentationAsync()))
        {
            WhyNot = _ => SourcePath is null || !Editor.IsVisible ? "No file is open" : null,
            Words = ["Quick Info", "Documentation", "Hover", "Javadoc", "KDoc"],
        });

        Editor.Hovered += (_, e) =>
        {
            if (e.Hint is not null)
            {
                CloseQuickDoc();
                return;
            }
            if (e.Annotation is not null)
            {
                return;
            }
            if (Session?.IsPaused == true && FrameShown() is not null)
            {
                return;
            }
            if (completing is not null || completionPopup?.IsOpen == true)
            {
                return;
            }
            if (quickDocCard is { IsOpen: true, IsSticky: true })
            {
                return;
            }
            IdeErrors.Observe(ShowQuickDocAtAsync(e.Offset, e.Point, sticky: false, startServer: false));
        };

        Editor.HoverEnded += (_, _) =>
        {
            if (quickDocCard is not { IsSticky: true })
            {
                quickDocAsk?.Cancel();
                quickDocAsk = null;
                quickDocCard?.ScheduleClose(TimeSpan.FromMilliseconds(300));
            }
        };

        Deactivated += (_, _) => CloseQuickDoc();
    }

    private void CloseQuickDoc()
    {
        quickDocAsk?.Cancel();
        quickDocAsk = null;
        quickDocTarget = null;
        quickDocCard?.Close();
    }

    /// <summary>Quick Documentation (<c>Ctrl+Q</c>): shows the IDEA-style documentation card at the caret in sticky mode.</summary>
    public async Task QuickDocumentationAsync()
    {
        if (SourcePath is not { } path || !Editor.IsVisible)
        {
            return;
        }
        CloseCompletion();
        CloseHintCard();
        int caret = Editor.Selections.Primary.Caret;
        TextPosition position = Editor.Document.Snapshot.GetPosition(caret);
        if (quickDocCard is { IsOpen: true, IsSticky: false } && quickDocTarget == (path, position))
        {
            quickDocCard.MakeSticky();
            return;
        }
        bool shown = await ShowQuickDocAtAsync(caret, Editor.PointOf(caret), sticky: true, startServer: true);
        if (!shown && SourcePath == path)
        {
            StatusText = HoverProvider is null && Language(path) is null
                ? $"No documentation available for {Path.GetFileName(path)}"
                : HoverProvider is null && !SemanticAvailable()
                    ? "Documentation is available once the folder is trusted and its project read"
                    : "No documentation found at the caret";
        }
    }

    /// <summary>Requests hover documentation at <paramref name="offset"/> and shows <see cref="QuickDocCard"/> if the
    /// server returns documentation while the document and request remain current.</summary>
    public async Task<bool> ShowQuickDocAtAsync(int offset, Point point, bool sticky, bool startServer)
    {
        quickDocAsk?.Cancel();
        CancellationTokenSource ask = CancellationTokenSource.CreateLinkedTokenSource(semanticLifetime.Token);
        quickDocAsk = ask;
        TextDocument document = Editor.Document;
        try
        {
            if (SourcePath is not { } path || !Editor.IsVisible)
            {
                return false;
            }
            TextSnapshot snapshot = document.Snapshot;
            int clamped = Math.Clamp(offset, 0, snapshot.Length);
            TextPosition position = snapshot.GetPosition(clamped);
            string? markdown = await HoverMarkdownAtAsync(path, document, position, startServer, ask.Token);
            if (ask.IsCancellationRequested || SourcePath != path || !ReferenceEquals(Editor.Document, document)
                || !ReferenceEquals(document.Snapshot, snapshot) || string.IsNullOrWhiteSpace(markdown))
            {
                if (!sticky && !ask.IsCancellationRequested)
                {
                    quickDocCard?.ScheduleClose(TimeSpan.FromMilliseconds(200));
                }
                return false;
            }
            if (!sticky && (completing is not null || completionPopup?.IsOpen == true || (Session?.IsPaused == true && FrameShown() is not null)))
            {
                return false;
            }

            string language = Language(path) ?? string.Empty;
            QuickDoc doc = QuickDocParser.Parse(markdown, language);
            if (doc.IsEmpty)
            {
                return false;
            }

            CloseHintCard();
            quickDocTarget = (path, position);
            double lineTop = Editor.PointOf(clamped).Y;
            Point anchor = new(point.X, lineTop + Editor.LineHeight + 4);
            string? footer = QuickDocFooter(doc);
            bool canGo = DeclarationFinder is not null || (Language(path) is not null && SemanticAvailable());
            QuickDocCard.Show(doc, anchor, lineTop, footer, sticky, canGo);
            return QuickDocCard.IsOpen;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(quickDocAsk, ask))
            {
                quickDocAsk = null;
            }
            ask.Dispose();
        }
    }

    private async Task<string?> HoverMarkdownAtAsync(string path, TextDocument document, TextPosition position, bool startServer, CancellationToken cancel)
    {
        if (HoverProvider is { } provider)
        {
            return await provider(path, position, cancel);
        }
        if (Language(path) is null || (startServer ? !SemanticAvailable() : RunningServerFor(path) is null))
        {
            return null;
        }
        JvmLanguageClient client = await LanguageClientAsync(path, cancel);
        if (!sentSources.TryGetValue(path, out (int Version, TextSnapshot Text) sent) || !ReferenceEquals(sent.Text, document.Snapshot))
        {
            return null;
        }
        JsonElement answer = await client.RequestAsync("textDocument/hover",
            new { textDocument = DocumentId(path), position = Point(position) }, cancel);
        string markdown = JvmInlayHints.Hover(answer);
        return markdown.Length > 0 ? markdown : null;
    }

    private string? QuickDocFooter(QuickDoc doc)
    {
        JvmModule? module = CurrentModule();
        if (IsJdkOwner(doc.Owner))
        {
            if (ModuleJdkName(module) is { Length: > 0 } jdk)
            {
                return $"< {jdk} >";
            }
        }
        if (module is { Name.Length: > 0 })
        {
            return module.Name;
        }
        if (ModuleJdkName(module) is { Length: > 0 } fallbackJdk)
        {
            return $"< {fallbackJdk} >";
        }
        return null;
    }

    private static bool IsJdkOwner(string? owner) => owner is not null && (
        owner.StartsWith("java.", StringComparison.Ordinal)
        || owner.StartsWith("javax.", StringComparison.Ordinal)
        || owner.StartsWith("jdk.", StringComparison.Ordinal)
        || owner.StartsWith("sun.", StringComparison.Ordinal)
        || owner.StartsWith("org.w3c.", StringComparison.Ordinal)
        || owner.StartsWith("org.xml.", StringComparison.Ordinal)
        || owner.StartsWith("org.ietf.", StringComparison.Ordinal));

    private static string? ModuleJdkName(JvmModule? module)
    {
        JvmToolchain? toolchain = module?.CompileToolchain.Home is not null ? module.CompileToolchain
            : module?.RuntimeToolchain.Home is not null ? module.RuntimeToolchain
            : null;
        if (toolchain?.Home is { Length: > 0 } home)
        {
            if (JdkRegistry.Current.All().FirstOrDefault(j => PathNames.Same(j.Home, home)) is { } registered)
            {
                return registered.Name;
            }
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(home));
            if (folder.Length > 0)
            {
                return folder;
            }
        }
        if (toolchain is { Vendor.Length: > 0, LanguageVersion: int version })
        {
            return $"{toolchain.Vendor.ToLowerInvariant()}-{version}";
        }
        if (toolchain?.LanguageVersion is int major)
        {
            return $"jdk-{major}";
        }
        return null;
    }

    private void FollowQuickDocLink(Uri uri)
    {
        if (uri.IsFile)
        {
            string localPath = uri.LocalPath;
            if (!File.Exists(localPath))
            {
                StatusText = $"{localPath} is not there";
                return;
            }
            int line = 0;
            if (uri.Fragment is { Length: > 1 } fragment
                && int.TryParse(fragment.TrimStart('#', 'L', 'l'), NumberStyles.None, CultureInfo.InvariantCulture, out int oneBased)
                && oneBased > 0)
            {
                line = oneBased - 1;
            }
            CloseQuickDoc();
            NavigateTo(localPath, new TextPosition(line, 0));
            return;
        }
        FollowLink(new WebLink(uri));
    }

    private async Task GoToQuickDocDeclarationAsync()
    {
        if (quickDocTarget is not { } target)
        {
            await GoToDeclarationAsync();
            return;
        }
        (string Path, TextPosition At)? found = await DeclarationAsync(target.Path, target.Position, startServer: true, semanticLifetime.Token);
        if (found is { } declared)
        {
            NavigateTo(declared.Path, declared.At);
        }
        else
        {
            StatusText = "No declaration found";
        }
    }
}
