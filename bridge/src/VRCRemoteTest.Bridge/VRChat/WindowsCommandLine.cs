using System.Text;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Splits a Windows command line the way the Microsoft C runtime builds argv, so a flag
/// is matched as a whole argument: a substring check would also accept, for example, a
/// --url value or a quoted path that merely contains "--watch-worlds".
/// </summary>
internal static class WindowsCommandLine
{
    public static bool ContainsArgument(string? commandLine, string argument) =>
        commandLine is not null && Split(commandLine).Skip(1).Contains(argument, StringComparer.Ordinal);

    /// <summary>
    /// argv[0] ends at the first whitespace, or at the closing quote when it starts with
    /// one, with no escape processing. After that: whitespace outside quotes separates
    /// arguments; 2n backslashes before a quote become n backslashes and the quote toggles
    /// quoting; 2n+1 backslashes before a quote become n backslashes and a literal quote;
    /// "" inside quotes is a literal quote; other backslashes are literal.
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var arguments = new List<string>();
        var i = 0;

        var program = new StringBuilder();
        if (i < commandLine.Length && commandLine[i] == '"')
        {
            i++;
            while (i < commandLine.Length && commandLine[i] != '"')
            {
                program.Append(commandLine[i++]);
            }

            i++; // closing quote, if any
        }
        else
        {
            while (i < commandLine.Length && !IsWhitespace(commandLine[i]))
            {
                program.Append(commandLine[i++]);
            }
        }

        arguments.Add(program.ToString());

        while (true)
        {
            while (i < commandLine.Length && IsWhitespace(commandLine[i]))
            {
                i++;
            }

            if (i >= commandLine.Length)
            {
                return arguments;
            }

            var current = new StringBuilder();
            var inQuotes = false;
            while (i < commandLine.Length && (inQuotes || !IsWhitespace(commandLine[i])))
            {
                var c = commandLine[i];
                if (c == '\\')
                {
                    var backslashes = 0;
                    while (i < commandLine.Length && commandLine[i] == '\\')
                    {
                        backslashes++;
                        i++;
                    }

                    if (i < commandLine.Length && commandLine[i] == '"')
                    {
                        current.Append('\\', backslashes / 2);
                        if (backslashes % 2 == 1)
                        {
                            current.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        current.Append('\\', backslashes);
                    }
                }
                else if (c == '"')
                {
                    if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                    {
                        current.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        i++;
                    }
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }

            arguments.Add(current.ToString());
        }
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t';
}
