// From Passenger 2044e49b src/Passenger.App/Run/LaunchProfile.cs (CommandLine), MIT (notices/Passenger/LICENSE).
using System.Text;

namespace WayfarerWanderer.App.Shell.Terminal;

/// <summary>A command line split into its words as Windows' and the shells' quoting reads them: Settings' Shell path.</summary>
public static class CommandLine
{
    /// <summary>Whether a line that says something names no program to start: its first word is empty ("" alone).</summary>
    public static bool NamesNoProgram(string? line) => !string.IsNullOrWhiteSpace(line) && string.IsNullOrWhiteSpace(Split(line)[0]);

    public static IReadOnlyList<string> Split(string? line)
    {
        List<string> arguments = [];
        if (string.IsNullOrWhiteSpace(line))
        {
            return arguments;
        }
        StringBuilder current = new();
        bool quoted = false;
        bool started = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                int run = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    run++;
                    i++;
                }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else
                {
                    current.Append('\\', run);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                // Two quotes inside a quoted run are one literal quote.
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
        {
            arguments.Add(current.ToString());
        }
        return arguments;
    }
}
