using System;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Globalization;
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
            configPath = inlineConfigPath is not null
                ? inlineConfigPath[(inlineConfigPath.IndexOf('=') + 1)..]
                : newArgsList.Contains("--config-file")
                    ? newArgsList.ElementAt(newArgsList.IndexOf("--config-file") + 1)
                    : Path.Combine(Program.APP_DIR, "BBDownT.config");
            if (File.Exists(configPath))
            {
                Log($"加载配置文件: {configPath}");
                var configLines = File.ReadAllLines(configPath);
                var normalizedConfigLines = configLines.Select(line => line.Trim()).ToArray();
                var configArgs = normalizedConfigLines
                    .Where(s => s.Length > 0 && !s.StartsWith('#'))
                    .SelectMany(s =>
                        {
                            var trimLine = s.Trim();
                            if (trimLine.StartsWith('-') && trimLine.Contains(' '))
                            {
                                var spaceIndex = trimLine.IndexOf(' ');
                                var paramsGroup = new[] { trimLine[..spaceIndex], trimLine[spaceIndex..] };
                                return paramsGroup.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.Trim(' ').Trim('\"'));
                            }
                            return [trimLine.Trim('\"')];
                        }
                    );
                var configArgsArray = configArgs.ToArray();
                var knownAliases = rootCommand.Options
                    .Concat(rootCommand.Children.OfType<Command>().SelectMany(command => command.Options))
                    .SelectMany(option => option.Aliases)
                    .ToHashSet(StringComparer.Ordinal);
                var unknownOptions = normalizedConfigLines
                    .Where(IsOptionDeclaration)
                    .Select(line => line.Split([' ', '='], 2)[0])
                    .Where(option => !knownAliases.Contains(option))
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
                string[] configValidationArgs = string.IsNullOrEmpty(commandName)
                    ? ["config-validation-placeholder", .. configArgsArray]
                    : [commandName, .. configArgsArray];
                var configArgsResult = rootCommand.Parse(configValidationArgs);
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

                var orderedOptionResults = CommandLineOptionOrder.ByAppearance(
                    configArgsResult.CommandResult.Children.OfType<OptionResult>(),
                    configArgsArray);
                foreach (var o in orderedOptionResults)
                {
                    if (!CommandLineOptionOrder.ContainsOption(newArgsList, o.Option.Aliases))
                    {
                        newArgsList.Add("--" + o.Option.Name);
                        newArgsList.AddRange(o.Tokens.Select(t => t.Value));
                    }
                }

                //命令行的优先级>配置文件优先级
                LogDebug("新的命令行参数: " + CommandLineLogSanitizer.Format(newArgsList));
            }
            return true;
        }
        catch (Exception ex)
        {
            LogError($"配置文件读取异常 ({configPath ?? "路径未解析"}): {CommandLineLogSanitizer.SanitizeText(ex.Message)}");
            return false;
        }
    }

    private static bool IsOptionDeclaration(string line)
    {
        return line.StartsWith('-')
            && !double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
