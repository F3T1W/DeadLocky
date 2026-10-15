using System.Text.RegularExpressions;

namespace DeadLocky.Features.ManageMods.Api;

internal static partial class GameSearchPaths
{
    public const string ModPath = "citadel/DeadLockyAddons";
    private const string OwnedLine = "\t\t\tGame\t\t\t\t\"" + ModPath + "\"";

    public static string SetEnabled(string text, bool enabled)
    {
        MatchCollection blocks = SearchPathsBlock().Matches(text);
        if (blocks.Count != 1)
        {
            throw new IOException("Unable to locate a unique SearchPaths block in gameinfo.gi.");
        }

        string clean = OwnedSearchPath().Replace(text, "");
        if (!enabled)
        {
            return clean;
        }

        Match block = SearchPathsBlock().Match(clean);
        int start = block.Index + block.Length;
        int end = clean.IndexOf('}', start);
        if (end < 0)
        {
            throw new IOException("The SearchPaths block in gameinfo.gi is incomplete.");
        }

        string entries = clean[start..end];
        string newline = clean.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string inserted = newline + OwnedLine;
        // Source 2 otherwise uses the first Game path as MOD and DEFAULT_WRITE_PATH.
        if (!HasKey(entries, "Mod"))
        {
            inserted += newline + "\t\t\tMod\t\t\t\t\"citadel\" // DeadLocky";
        }

        if (!HasKey(entries, "Write") && !HasKey(entries, "Default_Write_Path"))
        {
            inserted += newline + "\t\t\tWrite\t\t\t\"citadel\" // DeadLocky";
        }

        return clean.Insert(start, inserted);
    }

    private static bool HasKey(string entries, string key)
    {
        return SearchPathKey().Matches(entries).Any(match => match.Groups[1].Value.Split('+')
            .Contains(key, StringComparer.OrdinalIgnoreCase));
    }

    [GeneratedRegex("(?m)^[ \\t]*\\\"?SearchPaths\\\"?[ \\t\\r\\n]*\\{")]
    private static partial Regex SearchPathsBlock();

    [GeneratedRegex("(?m)^[ \\t]*\\\"?([A-Za-z_+]+)\\\"?[ \\t]+")]
    private static partial Regex SearchPathKey();

    [GeneratedRegex("(?m)^[ \\t]*(?:\\\"?Game\\\"?[ \\t]+\\\"?citadel/DeadLockyAddons\\\"?"
                    + "|\\\"?(?:Mod|Write)\\\"?[ \\t]+\\\"citadel\\\"[ \\t]+// DeadLocky)[ \\t]*\\r?\\n")]
    private static partial Regex OwnedSearchPath();
}
