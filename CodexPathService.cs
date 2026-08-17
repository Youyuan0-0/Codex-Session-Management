namespace CodexSessionHotSync;

internal static class CodexPathService
{
    public static string ToDesktopPath(string? value)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..].Replace('/', '\\');
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..].Replace('\\', '/');
        }

        return path;
    }

    public static IReadOnlyList<string> NormalizeDistinctPaths(IEnumerable<string> paths)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string path in paths)
        {
            string normalized = ToDesktopPath(path);
            if (normalized.Length == 0)
            {
                continue;
            }

            string comparisonKey = normalized.Replace('/', '\\').TrimEnd('\\');
            if (seen.Add(comparisonKey))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
