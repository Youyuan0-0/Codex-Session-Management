using System.Text.RegularExpressions;

namespace CodexSessionHotSync;

internal static partial class CodexConfigService
{
    public static string DefaultCodexHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex");

    public static string NormalizeCodexHome(string? value)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? DefaultCodexHome : value.Trim().Trim('"');
        candidate = Environment.ExpandEnvironmentVariables(candidate);
        if (candidate.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                candidate[2..]);
        }

        return Path.GetFullPath(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public static IReadOnlyList<DatabaseLocation> DatabaseLocations(string codexHome) =>
    [
        new("legacy", "根目录数据库", Path.Combine(codexHome, "state_5.sqlite"), "state_5.sqlite"),
        new("modern", "sqlite 目录数据库", Path.Combine(codexHome, "sqlite", "state_5.sqlite"), Path.Combine("sqlite", "state_5.sqlite")),
    ];

    public static (string CurrentProvider, IReadOnlyList<string> Providers) ReadProviders(string codexHome)
    {
        string configPath = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(configPath))
        {
            return ("openai", ["openai"]);
        }

        string text = File.ReadAllText(configPath);
        Match currentMatch = RootProviderRegex().Match(text);
        string current = currentMatch.Success ? currentMatch.Groups[1].Value : "openai";
        HashSet<string> providers = new(StringComparer.OrdinalIgnoreCase) { current, "openai" };
        foreach (Match match in ProviderSectionRegex().Matches(text))
        {
            if (!string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                providers.Add(match.Groups[1].Value.Trim('"'));
            }
        }

        return (current, providers.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [GeneratedRegex("(?m)^\\s*model_provider\\s*=\\s*[\"']([^\"']+)[\"']\\s*(?:#.*)?$")]
    private static partial Regex RootProviderRegex();

    [GeneratedRegex("(?m)^\\s*\\[model_providers\\.([^\\]]+)\\]\\s*$")]
    private static partial Regex ProviderSectionRegex();
}
