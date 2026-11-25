namespace WayfarerWanderer.Jvm;

/// <summary>JDT LS's inlay hints, part of every settings document sent it (it takes each document whole, so one without
/// them would turn them back to its defaults): parameter names before literal arguments, its own default, and the types
/// of <c>var</c> names and lambda parameters, which it leaves off unless asked. Both kinds are always asked for; the
/// editor draws those Settings show. Its signature help is turned on with them, off by default: a parameter name hint's
/// hover card is the signature of the call at its argument.</summary>
public static class JdtInlayHints
{
    public const string ParameterNames = "literals";

    public static object SignatureHelp { get; } = new { enabled = true };

    public static object Settings { get; } = new
    {
        parameterNames = new { enabled = ParameterNames },
        variableTypes = new { enabled = true },
        parameterTypes = new { enabled = true },
    };

    public static object? Value(string? section) => section switch
    {
        "java.inlayHints.parameterNames.enabled" => ParameterNames,
        "java.inlayHints.variableTypes.enabled" or "java.inlayHints.parameterTypes.enabled" or "java.signatureHelp.enabled" => true,
        _ => null,
    };
}
