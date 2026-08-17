using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSessionHotSync;

internal sealed class JsonlSessionService
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };
    private sealed record LineReplacement(int Start, int End, byte[] Contents);

    public async Task<SessionScanResult> ScanAsync(
        string codexHome,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        List<SessionRecord> sessions = [];
        List<string> unreadable = [];
        List<string> invalid = [];
        Dictionary<string, int> providerCounts = new(StringComparer.OrdinalIgnoreCase);
        string[] directories = includeArchived ? ["sessions", "archived_sessions"] : ["sessions"];

        foreach (string directoryName in directories)
        {
            string directoryPath = Path.Combine(codexHome, directoryName);
            if (!Directory.Exists(directoryPath))
            {
                continue;
            }

            foreach (string filePath in Directory.EnumerateFiles(
                         directoryPath,
                         "rollout-*.jsonl",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    SessionRecord? record = await ReadRecordAsync(
                        codexHome,
                        directoryName,
                        filePath,
                        cancellationToken);
                    if (record is null)
                    {
                        invalid.Add(filePath);
                        continue;
                    }

                    sessions.Add(record);
                    providerCounts[record.ModelProvider] = providerCounts.GetValueOrDefault(record.ModelProvider) + 1;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    unreadable.Add(filePath);
                }
            }
        }

        return new SessionScanResult
        {
            Sessions = sessions.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            UnreadableFiles = unreadable.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            InvalidFiles = invalid.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProviderCounts = providerCounts,
        };
    }

    public static IReadOnlyList<SessionRecord> SelectCanonicalSessions(IEnumerable<SessionRecord> sessions)
    {
        return sessions
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.Archived)
                .ThenByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Length)
                .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<SessionRecord>> FindProviderChangeCandidatesAsync(
        IReadOnlyList<SessionRecord> sessions,
        string targetProvider,
        CancellationToken cancellationToken = default)
    {
        List<SessionRecord> candidates = [];
        foreach (SessionRecord session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await HasMismatchedSessionMetaAsync(session.FilePath, targetProvider, cancellationToken))
                {
                    candidates.Add(session);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return candidates;
    }

    public async Task<JsonlProviderUpdateResult> UpdateProvidersAsync(
        IReadOnlyList<SessionRecord> sessions,
        string targetProvider,
        CancellationToken cancellationToken = default)
    {
        List<string> changed = [];
        List<string> skipped = [];

        foreach (SessionRecord session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await UpdateAllSessionMetaProvidersAsync(session, targetProvider, cancellationToken))
                {
                    changed.Add(session.FilePath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                skipped.Add(session.FilePath);
            }
        }

        return new JsonlProviderUpdateResult
        {
            ChangedPaths = changed,
            SkippedPaths = skipped,
        };
    }

    public async Task<bool> HasUserEventAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, true, 65536, false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 ||
                (!line.Contains("user_message", StringComparison.Ordinal) &&
                 !line.Contains("\"role\"", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject record && RecordHasUserEvent(record))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static async Task<SessionRecord?> ReadRecordAsync(
        string codexHome,
        string directoryName,
        string filePath,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(filePath);
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, true, 65536, false);
        JsonObject? root = null;
        JsonObject? payload = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject candidate &&
                    string.Equals(GetString(candidate["type"]), "session_meta", StringComparison.Ordinal) &&
                    candidate["payload"] is JsonObject candidatePayload)
                {
                    root = candidate;
                    payload = candidatePayload;
                    break;
                }
            }
            catch (JsonException)
            {
            }
        }

        if (root is null || payload is null)
        {
            return null;
        }

        string id = GetString(payload["id"])?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        DateTimeOffset createdAt = ParseTimestamp(payload["timestamp"])
            ?? ParseTimestamp(root["timestamp"])
            ?? info.CreationTimeUtc;
        DateTimeOffset updatedAt = info.LastWriteTimeUtc;
        return new SessionRecord
        {
            Id = id,
            FilePath = Path.GetFullPath(filePath),
            RelativePath = Path.GetRelativePath(codexHome, filePath),
            DirectoryName = directoryName,
            Archived = string.Equals(directoryName, "archived_sessions", StringComparison.Ordinal),
            ModelProvider = GetString(payload["model_provider"]) ?? "(missing)",
            Cwd = CodexPathService.ToDesktopPath(GetString(payload["cwd"]) ?? string.Empty),
            Source = NodeText(payload["source"], "unknown"),
            CliVersion = GetString(payload["cli_version"]) ?? string.Empty,
            HistoryMode = GetString(payload["history_mode"]) ?? "legacy",
            ThreadSource = payload["thread_source"] is null ? null : NodeText(payload["thread_source"], string.Empty),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Length = info.Length,
        };
    }

    private static async Task<bool> HasMismatchedSessionMetaAsync(
        string filePath,
        string targetProvider,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, true, 65536, false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.Contains("session_meta", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject record &&
                    string.Equals(GetString(record["type"]), "session_meta", StringComparison.Ordinal) &&
                    record["payload"] is JsonObject payload &&
                    !string.Equals(GetString(payload["model_provider"]), targetProvider, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> UpdateAllSessionMetaProvidersAsync(
        SessionRecord session,
        string targetProvider,
        CancellationToken cancellationToken)
    {
        byte[] original = await File.ReadAllBytesAsync(session.FilePath, cancellationToken);
        int bomLength = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF
            ? 3
            : 0;
        List<LineReplacement> replacements = [];
        int lineStart = bomLength;
        for (int index = bomLength; index <= original.Length; index++)
        {
            if (index < original.Length && original[index] != (byte)'\n')
            {
                continue;
            }

            int contentEnd = index;
            int lineEnd = contentEnd > lineStart && original[contentEnd - 1] == (byte)'\r'
                ? contentEnd - 1
                : contentEnd;
            if (lineEnd > lineStart)
            {
                string line = Encoding.UTF8.GetString(original, lineStart, lineEnd - lineStart);
                if (line.Contains("session_meta", StringComparison.Ordinal))
                {
                    try
                    {
                        if (JsonNode.Parse(line) is JsonObject record &&
                            string.Equals(GetString(record["type"]), "session_meta", StringComparison.Ordinal) &&
                            record["payload"] is JsonObject payload &&
                            !string.Equals(GetString(payload["model_provider"]), targetProvider, StringComparison.Ordinal))
                        {
                            payload["model_provider"] = targetProvider;
                            replacements.Add(new LineReplacement(
                                lineStart,
                                lineEnd,
                                Encoding.UTF8.GetBytes(record.ToJsonString(CompactJson))));
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }

            lineStart = index + 1;
        }

        if (replacements.Count == 0)
        {
            return false;
        }

        FileInfo current = new(session.FilePath);
        if (current.Length != session.Length || current.LastWriteTimeUtc != session.UpdatedAt.UtcDateTime)
        {
            throw new IOException("会话文件在扫描后发生变化，请重试。");
        }

        string tempPath = session.FilePath + ".hot-sync-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream output = new(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65536,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                int cursor = 0;
                foreach (LineReplacement replacement in replacements)
                {
                    if (replacement.Start > cursor)
                    {
                        await output.WriteAsync(original.AsMemory(cursor, replacement.Start - cursor), cancellationToken);
                    }

                    await output.WriteAsync(replacement.Contents, cancellationToken);
                    cursor = replacement.End;
                }

                if (cursor < original.Length)
                {
                    await output.WriteAsync(original.AsMemory(cursor), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            current.Refresh();
            if (current.Length != session.Length || current.LastWriteTimeUtc != session.UpdatedAt.UtcDateTime)
            {
                throw new IOException("会话文件在写入前发生变化，请重试。");
            }

            File.Move(tempPath, session.FilePath, true);
            File.SetLastWriteTimeUtc(session.FilePath, session.UpdatedAt.UtcDateTime);
            return true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool RecordHasUserEvent(JsonObject record)
    {
        if (string.Equals(GetString(record["type"]), "event_msg", StringComparison.Ordinal) &&
            record["payload"] is JsonObject eventPayload &&
            string.Equals(GetString(eventPayload["type"]), "user_message", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (string key in new[] { "payload", "item", "msg" })
        {
            if (record[key] is JsonObject message &&
                string.Equals(GetString(message["type"]), "message", StringComparison.Ordinal) &&
                string.Equals(GetString(message["role"]), "user", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset? ParseTimestamp(JsonNode? node)
    {
        string? value = GetString(node);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
    }

    private static string NodeText(JsonNode? node, string fallback)
    {
        return GetString(node) ?? node?.ToJsonString(CompactJson) ?? fallback;
    }

    private static string? GetString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
