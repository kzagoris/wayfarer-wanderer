namespace WayfarerWanderer.App;

public sealed record AcceptanceOptions(string CaseFile, string Results)
{
    public static AcceptanceOptions? Parse(string[] arguments)
    {
        if (!arguments.Contains("--acceptance", StringComparer.Ordinal))
        {
            return null;
        }

        if (arguments.Length != 4 || arguments[0] != "--acceptance" || arguments[2] != "--results")
        {
            throw new FormatException("Expected --acceptance <case-file> --results <directory>.");
        }
        return new AcceptanceOptions(Path.GetFullPath(arguments[1]), Path.GetFullPath(arguments[3]));
    }
}
