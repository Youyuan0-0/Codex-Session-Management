using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSessionHotSync;

internal sealed class SessionIndexService
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public async Task<(int Entries, IReadOnlyDictionary<string, string> Names)> ReadSummaryAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(codexHome, "session_index.jsonl");
        if (!File.Exists(path))
        {
            return (0, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        Dictionary<string, string> names = new(StringComparer.Ordinal);
        int entries = 0;
        foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (!TryParseEntry(line, out string? id, out JsonObject? data) || id is null || data is null)
            {
                continue;
            }

            entries++;
            string? name = GetString(data["thread_name"]);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[id] = name;
            }
        }

        return (entries, names);
    }

    public async Task<PreparedIndexFile> PrepareAsync(
        string codexHome,
        IReadOnlyList<SessionRecord> sessions,
        IReadOnlyDictionary<string, string> titles,
        CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(codexHome, "session_index.jsonl");
        byte[] original = File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken)
            : [];
        string text = original.Length == 0 ? string.Empty : Encoding.UTF8.GetString(original);
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        Dictionary<string, SessionIndexEntry> entries = new(StringComparer.Ordinal);
        List<(int Order, string Raw)> rawLines = [];
        int deduplicated = 0;
        int order = 0;
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseEntry(line, out string? id, out JsonObject? data) || id is null || data is null)
            {
                rawLines.Add((order++, line));
                continue;
            }

            SessionIndexEntry candidate = new(id, data, order++);
            if (!entries.TryGetValue(id, out SessionIndexEntry? existing))
            {
                entries[id] = candidate;
                continue;
            }

            deduplicated++;
            if (UpdatedAt(candidate.Data) >= UpdatedAt(existing.Data))
            {
                entries[id] = candidate with { OriginalOrder = existing.OriginalOrder };
            }
        }

        int added = 0;
        foreach (SessionRecord session in sessions.OrderBy(item => item.UpdatedAt))
        {
            if (entries.TryGetValue(session.Id, out SessionIndexEntry? existing))
            {
                if (string.IsNullOrWhiteSpace(GetString(existing.Data["thread_name"])) &&
                    titles.TryGetValue(session.Id, out string? existingTitle) &&
                    !string.IsNullOrWhiteSpace(existingTitle))
                {
                    existing.Data["thread_name"] = existingTitle;
                }

                continue;
            }

            string? title = titles.GetValueOrDefault(session.Id);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "会话 " + session.Id[..Math.Min(8, session.Id.Length)];
            }

            JsonObject data = new()
            {
                ["id"] = session.Id,
                ["thread_name"] = title,
                ["updated_at"] = session.UpdatedAt.UtcDateTime.ToString("O"),
            };
            entries[session.Id] = new SessionIndexEntry(session.Id, data, order++);
            added++;
        }

        List<(int Order, string Line)> output = entries.Values
            .Select(item => (Order: item.OriginalOrder, Line: item.Data.ToJsonString(CompactJson)))
            .Concat(rawLines.Select(item => (Order: item.Order, Line: item.Raw)))
            .OrderBy(item => item.Order)
            .ToList();
        string finalText = output.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, output.Select(item => item.Line)) + Environment.NewLine;
        byte[] contents = Encoding.UTF8.GetBytes(finalText);
        bool changed = !original.AsSpan().SequenceEqual(contents);

        return new PreparedIndexFile
        {
            Path = path,
            Contents = contents,
            Result = new SessionIndexUpdateResult(entries.Count, added, deduplicated, changed),
        };
    }

    public async Task WriteAsync(PreparedIndexFile prepared, CancellationToken cancellationToken = default)
    {
        if (!prepared.Result.Changed)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(prepared.Path)!);
        string tempPath = prepared.Path + ".hot-sync-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, prepared.Contents, cancellationToken);
            File.Move(tempPath, prepared.Path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool TryParseEntry(string line, out string? id, out JsonObject? data)
    {
        id = null;
        data = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            data = JsonNode.Parse(line) as JsonObject;
            id = GetString(data?["id"]);
            return data is not null && !string.IsNullOrWhiteSpace(id);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTimeOffset UpdatedAt(JsonObject data)
    {
        return DateTimeOffset.TryParse(GetString(data["updated_at"]), out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
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
