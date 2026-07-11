using System;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using System.Linq;

namespace BBDownT;

internal static class CommandLineOptionOrder
{
    private static readonly string[] EncodingPriorityAliases = ["--encoding-priority", "-e"];
    private static readonly string[] DfnPriorityAliases = ["--dfn-priority", "-q"];

    internal static IEnumerable<OptionResult> ByAppearance(
        IEnumerable<OptionResult> optionResults,
        IReadOnlyList<string> arguments)
    {
        return optionResults.OrderBy(result => FindFirst(arguments, result.Option.Aliases));
    }

    internal static bool ContainsOption(IEnumerable<string> arguments, IEnumerable<string> aliases)
    {
        var aliasArray = aliases.ToArray();
        return arguments.Any(argument => MatchesAnyAlias(argument, aliasArray));
    }

    internal static bool EncodingPrecedesQuality(IEnumerable<string> arguments)
    {
        var argumentArray = arguments.ToArray();
        var encodingIndex = FindFirst(argumentArray, EncodingPriorityAliases);
        var qualityIndex = FindFirst(argumentArray, DfnPriorityAliases);
        return encodingIndex < qualityIndex;
    }

    private static int FindFirst(IReadOnlyList<string> arguments, IEnumerable<string> aliases)
    {
        var aliasArray = aliases.ToArray();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (MatchesAnyAlias(arguments[index], aliasArray))
            {
                return index;
            }
        }
        return int.MaxValue;
    }

    private static bool MatchesAnyAlias(string argument, IReadOnlyList<string> aliases)
    {
        return aliases.Any(alias =>
            string.Equals(argument, alias, StringComparison.Ordinal)
            || argument.StartsWith(alias + "=", StringComparison.Ordinal));
    }
}
