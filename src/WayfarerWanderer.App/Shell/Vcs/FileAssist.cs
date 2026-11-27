using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>One suggestion a file's own help offers: what is put in, its kind as the protocol numbers one (17 a file, 19 a
/// folder, 10 a property, 20 a value), the few words beside it, and what its card says.</summary>
public sealed record AssistItem(string Label, int Kind, string? Detail, string Said);

/// <summary>The editor's help for a kind of file no language server reads, which Wayfarer answers for itself (after
/// Passenger's <c>LanguageAssist</c>): what completion offers and where it goes, what the hover card says of a line, and
/// the marks its text gets. Every answer is of the text given, so a caller checks the text has not moved on since.</summary>
public interface IFileAssist
{
    /// <summary>Whether the file at <paramref name="path"/> is one this helps with.</summary>
    bool Owns(string path);

    /// <summary>The characters beyond a name's that narrow the list rather than close it.</summary>
    string WordCharacters { get; }

    /// <summary>Where an item put at <paramref name="caret"/> goes, from its start to the end of what it replaces; null
    /// where nothing goes.</summary>
    (int Start, int End)? Spot(string text, int caret);

    /// <summary>Whether <paramref name="typed"/>, just typed with the caret now after it, opens the list.</summary>
    bool Opens(string text, int caret, char typed);

    /// <summary>The suggestions at <paramref name="caret"/>, in the order they are listed; empty for none.</summary>
    Task<IReadOnlyList<AssistItem>> ItemsAsync(string path, string text, int caret, CancellationToken cancel);

    /// <summary>The hover card at <paramref name="offset"/> as Markdown; null for none. <paramref name="modified"/> says
    /// the text differs from the file saved.</summary>
    Task<string?> HoverAsync(string path, TextSnapshot text, bool modified, int offset, CancellationToken cancel);

    /// <summary>The marks the text gets.</summary>
    Task<IReadOnlyList<Marker>> MarksAsync(string path, TextSnapshot text, CancellationToken cancel);
}
