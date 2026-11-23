// From Passenger 8a1df7e4 src/Passenger.App/KindBadges.cs, MIT (notices/Passenger/LICENSE): read from Wayfarer's own
// SymbolKind (a Java package and a module a namespace's) and the protocol's completion item kinds by number; C#'s extension
// methods and named arguments, Godot's nodes and Markdown's headings cut.
using Passenger.Editor;
using WayfarerWanderer.App.Shell.Navigation;

namespace WayfarerWanderer.App.Shell;

/// <summary>What a kind badge marks: a kind of symbol, of member or of completion item.</summary>
public enum BadgeKind
{
    Other,
    Namespace,
    Class,
    Interface,
    Struct,
    Enum,
    EnumMember,
    TypeParameter,
    Method,
    Property,
    Field,
    Constant,
    Event,
    Operator,
    Local,
    Parameter,
    Keyword,
    Template,
}

/// <summary>The one map from a kind to its badge, its letter on its kind's colour (the theme's resource of that key), so a
/// kind shows alike wherever a badge marks it: Go to Symbol and File Structure, the navigation bar, completion. The
/// protocol's kinds of symbol and of completion item are read into it here too.</summary>
public static class KindBadges
{
    public static (string Letter, string Brush) Of(BadgeKind kind) => kind switch
    {
        BadgeKind.Namespace => ("N", TokenKeys.Syntax_Keyword),
        BadgeKind.Class => ("C", TokenKeys.Syntax_Class),
        BadgeKind.Interface => ("I", TokenKeys.Syntax_Interface),
        BadgeKind.Struct => ("S", TokenKeys.Syntax_Struct),
        BadgeKind.Enum => ("E", TokenKeys.Syntax_Enum),
        BadgeKind.EnumMember => ("e", TokenKeys.Syntax_EnumMember),
        BadgeKind.TypeParameter => ("T", TokenKeys.Syntax_TypeParameter),
        BadgeKind.Method => ("m", TokenKeys.Syntax_Method),
        BadgeKind.Property => ("p", TokenKeys.Syntax_Property),
        BadgeKind.Field => ("f", TokenKeys.Syntax_Field),
        BadgeKind.Constant => ("c", TokenKeys.Syntax_Constant),
        BadgeKind.Event => ("e", TokenKeys.Syntax_Event),
        BadgeKind.Operator => ("o", TokenKeys.TextMuted),
        BadgeKind.Local => ("v", TokenKeys.TextMuted),
        BadgeKind.Parameter => ("a", TokenKeys.TextMuted),
        BadgeKind.Keyword => ("k", TokenKeys.Syntax_Keyword),
        BadgeKind.Template => ("s", TokenKeys.TextMuted),
        _ => ("·", TokenKeys.TextMuted),
    };

    /// <summary>The badge of a symbol of the protocol's kind; a variable a local's, a constructor a method's, a package and a
    /// module a namespace's.</summary>
    public static BadgeKind Of(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace or SymbolKind.Package or SymbolKind.Module => BadgeKind.Namespace,
        SymbolKind.Class => BadgeKind.Class,
        SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor => BadgeKind.Method,
        SymbolKind.Property => BadgeKind.Property,
        SymbolKind.Field => BadgeKind.Field,
        SymbolKind.Enum => BadgeKind.Enum,
        SymbolKind.Interface => BadgeKind.Interface,
        SymbolKind.Variable => BadgeKind.Local,
        SymbolKind.Constant => BadgeKind.Constant,
        SymbolKind.EnumMember => BadgeKind.EnumMember,
        SymbolKind.Struct => BadgeKind.Struct,
        SymbolKind.Event => BadgeKind.Event,
        SymbolKind.Operator => BadgeKind.Operator,
        SymbolKind.TypeParameter => BadgeKind.TypeParameter,
        _ => BadgeKind.Other,
    };

    /// <summary>The badge of a completion item of the protocol's kind (its number: 2 a method, 7 a class...), read as its
    /// symbol's kind is: a variable a local's, a constructor a method's.</summary>
    public static BadgeKind OfCompletion(int kind) => kind switch
    {
        2 or 3 or 4 => BadgeKind.Method,
        5 => BadgeKind.Field,
        6 => BadgeKind.Local,
        7 => BadgeKind.Class,
        8 => BadgeKind.Interface,
        9 => BadgeKind.Namespace,
        10 => BadgeKind.Property,
        13 => BadgeKind.Enum,
        14 => BadgeKind.Keyword,
        15 => BadgeKind.Template,
        20 => BadgeKind.EnumMember,
        21 => BadgeKind.Constant,
        22 => BadgeKind.Struct,
        23 => BadgeKind.Event,
        24 => BadgeKind.Operator,
        25 => BadgeKind.TypeParameter,
        _ => BadgeKind.Other,
    };
}
