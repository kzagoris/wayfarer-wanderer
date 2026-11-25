namespace WayfarerWanderer.Jvm;

/// <summary>The Kotlin server's inlay hints, which it reads from the client's <c>jetbrains.kotlin</c> section before each
/// ask and shows none of without it: parameter names, and the types of names that do not write them (a local or a
/// property, a lambda's parameters, its implicit <c>it</c> and <c>this</c>, a function's expression body). The hints that
/// say neither a parameter nor a type (a lambda's result, a range's bounds, call chains) stay off. Both kinds are always
/// asked for; the editor draws those Settings show.</summary>
public static class KotlinInlayHints
{
    public const string Section = "jetbrains.kotlin";

    private static readonly (string Name, bool On)[] Options =
    [
        ("hints.parameters", true),
        ("hints.parameters.compiled", true),
        ("hints.parameters.excluded", false),
        ("hints.settings.types.property", true),
        ("hints.settings.types.variable", true),
        ("hints.type.function.return", true),
        ("hints.type.function.parameter", true),
        ("hints.lambda.receivers.parameters", true),
        ("hints.settings.lambda.return", false),
        ("hints.settings.value.ranges", false),
        ("hints.value.kotlin.time", false),
        ("hints.call.chains", false),
    ];

    /// <summary>The section as VS Code hands it over, its dotted names nested, and with each dotted name as well, so the
    /// server finds an option however it looks one up.</summary>
    public static object Settings { get; } = Build();

    private static Dictionary<string, object> Build()
    {
        Dictionary<string, object> section = [];
        foreach ((string name, bool on) in Options)
        {
            section[name] = on;
            Dictionary<string, object> level = section;
            string[] parts = name.Split('.');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!level.TryGetValue(parts[i], out object? next) || next is not Dictionary<string, object> inner)
                {
                    // A name that is itself an option (hints.parameters) is not also a level (VS Code keeps the option).
                    if (next is not null)
                    {
                        level = null!;
                        break;
                    }
                    level[parts[i]] = inner = [];
                }
                level = inner;
            }
            level?.TryAdd(parts[^1], on);
        }
        return section;
    }
}
