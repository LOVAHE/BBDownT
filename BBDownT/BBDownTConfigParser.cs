using System;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Globalization;
using BBDownT.Core.Util;
using static BBDownT.Core.Logger;

namespace BBDownT;

internal static class BBDownTConfigParser
{
    public static bool HandleConfig(List<string> newArgsList, RootCommand rootCommand, string? commandName = null)
    {
        string? configPath = null;
        try
        {
            var inlineConfigPath = newArgsList
                .FirstOrDefault(argument => argument.StartsWith("--config-file=", StringComparison.Ordinal));
            var explicitPath = inlineConfigPath is not null || newArgsList.Contains("--config-file");
            configPath = inlineConfigPath is not null
                ? inlineConfigPath[(inlineConfigPath.IndexOf('=') + 1)..]
                : explicitPath
                    ? newArgsList.ElementAt(newArgsList.IndexOf("--config-file") + 1)
                    : Path.Combine(Program.APP_DIR, "BBDownT.config");
            if (explicitPath && !File.Exists(configPath))
            {
                LogError($"配置文件不存在: {configPath}");
                return false;
            }
            if (File.Exists(configPath))
            {
                Log($"加载配置文件: {configPath}");
                var configLines = File.ReadAllLines(configPath)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#'));
                var optionsByAlias = OptionsByAlias(rootCommand, commandName);
                var entries = ReadEntries(configLines, optionsByAlias);
                var unknownOptions = entries
                    .Select(entry => entry.Alias)
                    .OfType<string>()
                    .Where(alias => !optionsByAlias.ContainsKey(alias))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (unknownOptions.Length > 0)
                {
                    foreach (var option in unknownOptions)
                    {
                        LogError($"配置文件错误 ({configPath}): 无法识别的配置项 {CommandLineLogSanitizer.SanitizeText(option)}");
                    }
                    return false;
                }
                var validationCommand = string.IsNullOrEmpty(commandName) ? "config-validation-placeholder" : commandName;
                var configArgsArray = ArgumentsForCommand(entries,
                        alias => !rootCommand.Parse([validationCommand, alias]).UnmatchedTokens.Contains(alias))
                    .ToArray();
                var configArgsResult = rootCommand.Parse([validationCommand, .. configArgsArray]);
                if (configArgsResult.Errors.Any())
                {
                    foreach (var error in configArgsResult.Errors)
                    {
                        LogError($"配置文件错误 ({configPath}): {CommandLineLogSanitizer.SanitizeText(error.Message)}");
                    }
                    return false;
                }
                if (configArgsResult.UnmatchedTokens.Any())
                {
                    foreach (var token in configArgsResult.UnmatchedTokens)
                    {
                        LogError($"配置文件错误 ({configPath}): 无法识别的配置项 {CommandLineLogSanitizer.SanitizeText(token)}");
                    }
                    return false;
                }

                var commandLine = rootCommand.Parse(newArgsList.ToArray());
                var orderedOptionResults = CommandLineOptionOrder.ByAppearance(
                    configArgsResult.CommandResult.Children.OfType<OptionResult>(),
                    configArgsArray);
                foreach (var o in orderedOptionResults)
                {
                    if (commandLine.FindResultFor(o.Option) is not { IsImplicit: false })
                    {
                        newArgsList.AddRange(CommandLineOptionOrder.Arguments(o));
                    }
                }

                //命令行的优先级>配置文件优先级
                LogDebug("新的命令行参数: " + CommandLineLogSanitizer.Format(newArgsList));
            }
            return true;
        }
        catch (Exception ex)
        {
            LogError($"配置文件读取异常 ({configPath ?? "路径未解析"}): {CommandLineLogSanitizer.SanitizeText(ErrorText.Describe(ex))}");
            return false;
        }
    }

    private sealed record ConfigEntry(string? Alias, string[] Arguments);

    private static Dictionary<string, Option> OptionsByAlias(RootCommand rootCommand, string? commandName)
    {
        var commands = rootCommand.Children.OfType<Command>().ToArray();
        var optionsByAlias = new Dictionary<string, Option>(StringComparer.Ordinal);
        foreach (var option in commands.Where(command => command.Name == commandName).SelectMany(command => command.Options)
            .Concat(rootCommand.Options)
            .Concat(commands.SelectMany(command => command.Options)))
        {
            foreach (var alias in option.Aliases) optionsByAlias.TryAdd(alias, option);
        }
        return optionsByAlias;
    }

    private static List<ConfigEntry> ReadEntries(IEnumerable<string> lines, IReadOnlyDictionary<string, Option> optionsByAlias)
    {
        var entries = new List<ConfigEntry>();
        string? awaitingValue = null;
        foreach (var line in lines)
        {
            if (awaitingValue is not null)
            {
                entries.Add(new(awaitingValue, WithValue(awaitingValue, line.Trim('"'))));
                awaitingValue = null;
            }
            else if (!IsOptionDeclaration(line))
            {
                entries.Add(new(null, [line.Trim('"')]));
            }
            else if (OptionAlias(line) is var alias && alias.Length < line.Length)
            {
                var value = line[(alias.Length + 1)..].Trim(' ');
                entries.Add(new(alias, value.Length > 0 ? WithValue(alias, value.Trim('"')) : [alias]));
            }
            else if (optionsByAlias.TryGetValue(line, out var option) && option.Arity.MinimumNumberOfValues > 0)
            {
                awaitingValue = line;
            }
            else
            {
                entries.Add(new(line, [line]));
            }
        }
        if (awaitingValue is not null) entries.Add(new(awaitingValue, [awaitingValue]));
        return entries;
    }

    private static string[] WithValue(string alias, string value) => value.Length > 0 ? [$"{alias}={value}"] : [alias, value];

    private static IEnumerable<string> ArgumentsForCommand(IEnumerable<ConfigEntry> entries, Func<string, bool> appliesToCommand)
    {
        var keep = true;
        foreach (var entry in entries)
        {
            if (entry.Alias is not null)
            {
                keep = appliesToCommand(entry.Alias);
                if (!keep) LogDebug($"配置项 {CommandLineLogSanitizer.SanitizeText(entry.Alias)} 不适用于当前命令，已忽略");
            }
            if (!keep) continue;
            foreach (var argument in entry.Arguments) yield return argument;
        }
    }

    private static string OptionAlias(string line) => line.Split([' ', '='], 2)[0];

    private static bool IsOptionDeclaration(string line)
    {
        return line.StartsWith('-')
            && !double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
