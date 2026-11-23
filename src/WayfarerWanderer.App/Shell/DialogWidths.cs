// From Passenger 8a1df7e4 src/Passenger.App/DialogWidths.cs, MIT (notices/Passenger/LICENSE).
// Cut: the mention of Passenger's UiRulesTests, which Wayfarer does not vendor; .NET dialogs among the examples.
namespace WayfarerWanderer.App.Shell;

/// <summary>How wide a dialog opens, one step for each kind, as a popup's are (<see cref="PopupFrame.Widths"/>): so a form
/// is one width wherever it opens and its fields end where the next form's do.</summary>
public static class DialogWidths
{
    /// <summary>A question, a pick from a short list, About.</summary>
    public const double Question = 520;

    /// <summary>A form of fields, a refactoring's.</summary>
    public const double Form = 560;

    /// <summary>A table to edit or a form with a list beside it.</summary>
    public const double Wide = 760;

    /// <summary>A workspace of a list and its page: Settings, New Project.</summary>
    public const double Workspace = 980;

    public static IReadOnlyList<double> All { get; } = [Question, Form, Wide, Workspace];
}
