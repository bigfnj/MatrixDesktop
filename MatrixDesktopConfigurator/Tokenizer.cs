using System.Collections.Generic;
using System.Text;

namespace MatrixDesktopConfigurator;

// Splits a command-line string into argument tokens, honouring quoted segments.
// Used by ArgumentImporter when parsing user-provided existing MatrixDesktop
// commands. Extracted to a dedicated file to make ArgumentImporter focus on the
// semantic mapping (tokens → draft fields) rather than the lexing step.
internal static class Tokenizer
{
    public static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var tokenStarted = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (tokenStarted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(c);
            tokenStarted = true;
        }

        if (tokenStarted)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
