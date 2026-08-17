using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSessionHotSync;

internal sealed record PreparedGlobalStateFile
{
    public required string Path { get; init; }
    public required bool Exists { get; init; }
    public required bool Changed { get; init; }
    public required byte[] Contents { get; init; }
    public required long OriginalLength { get; init; }
    public required DateTime OriginalWriteTimeUtc { get; init; }
    public required IReadOnlySet<string> ProjectlessThreadIds { get; init; }
    public required IReadOnlyList<string> WorkspaceRoots { get; init; }
}

internal sealed class CodexGlobalStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<PreparedGlobalStateFile> PrepareAsync(
        string codexHome,
        IReadOnlyCollection<string>? additionalWorkspaceRoots = null,
        IReadOnlyCollection<string>? additionalProjectlessThreadIds = null,
        CancellationToken cancellationToken = default)
    {
        string path = System.IO.Path.Combine(codexHome, ".codex-global-state.json");
        bool exists = File.Exists(path);
        if (!exists &&
            (additionalWorkspaceRoots is null || additionalWorkspaceRoots.Count == 0) &&
            (additionalProjectlessThreadIds is null || additionalProjectlessThreadIds.Count == 0))
        {
            return new PreparedGlobalStateFile
            {
                Path = path,
                Exists = false,
                Changed = false,
                Contents = [],
                OriginalLength = 0,
                OriginalWriteTimeUtc = DateTime.MinValue,
                ProjectlessThreadIds = new HashSet<string>(StringComparer.Ordinal),
                WorkspaceRoots = [],
            };
        }

        FileInfo? info = exists ? new FileInfo(path) : null;
        string text = exists ? await File.ReadAllTextAsync(path, cancellationToken) : "{}";
        JsonObject root = JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException(".codex-global-state.json 不是有效的 JSON 对象。");
        JsonObject normalized = (JsonObject)root.DeepClone();
        NormalizePathArray(normalized, "electron-saved-workspace-roots");
        NormalizePathArray(normalized, "project-order");
        NormalizeActiveWorkspaceRoots(normalized);
        NormalizeObjectKeys(normalized, "electron-workspace-root-labels");
        NormalizeOpenTargetPreferences(normalized);
        AddWorkspaceRoots(normalized, additionalWorkspaceRoots);
        AddProjectlessThreadIds(normalized, additionalProjectlessThreadIds);
        HashSet<string> projectless = ReadProjectlessThreadIds(normalized);
        IReadOnlyList<string> workspaceRoots = ReadWorkspaceRoots(normalized);
        bool changed = !JsonNode.DeepEquals(root, normalized);
        byte[] contents = changed
            ? Encoding.UTF8.GetBytes(normalized.ToJsonString(JsonOptions))
            : Encoding.UTF8.GetBytes(text);
        return new PreparedGlobalStateFile
        {
            Path = path,
            Exists = exists,
            Changed = changed,
            Contents = contents,
            OriginalLength = info?.Length ?? 0,
            OriginalWriteTimeUtc = info?.LastWriteTimeUtc ?? DateTime.MinValue,
            ProjectlessThreadIds = projectless,
            WorkspaceRoots = workspaceRoots,
        };
    }

    public async Task WriteAsync(
        PreparedGlobalStateFile prepared,
        CancellationToken cancellationToken = default)
    {
        if (!prepared.Changed)
        {
            return;
        }

        FileInfo current = new(prepared.Path);
        if (prepared.Exists &&
            (current.Length != prepared.OriginalLength || current.LastWriteTimeUtc != prepared.OriginalWriteTimeUtc))
        {
            throw new IOException("Codex 全局状态在扫描后发生变化，请重试。");
        }

        if (!prepared.Exists && File.Exists(prepared.Path))
        {
            throw new IOException("Codex 全局状态在扫描后被创建，请重试。");
        }

        string tempPath = prepared.Path + ".hot-sync-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, prepared.Contents, cancellationToken);
            current.Refresh();
            if (prepared.Exists &&
                (current.Length != prepared.OriginalLength || current.LastWriteTimeUtc != prepared.OriginalWriteTimeUtc))
            {
                throw new IOException("Codex 全局状态在写入前发生变化，请重试。");
            }

            if (!prepared.Exists && File.Exists(prepared.Path))
            {
                throw new IOException("Codex 全局状态在写入前被创建，请重试。");
            }

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

    private static void AddWorkspaceRoots(JsonObject root, IReadOnlyCollection<string>? additions)
    {
        if (additions is null || additions.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> normalized = CodexPathService.NormalizeDistinctPaths(
            ReadPaths(root["electron-saved-workspace-roots"]).Concat(additions));
        root["electron-saved-workspace-roots"] = new JsonArray(
            normalized.Select(path => JsonValue.Create(path)).ToArray());

        IReadOnlyList<string> order = CodexPathService.NormalizeDistinctPaths(
            ReadPaths(root["project-order"]).Concat(normalized));
        root["project-order"] = new JsonArray(order.Select(path => JsonValue.Create(path)).ToArray());
    }

    private static void AddProjectlessThreadIds(JsonObject root, IReadOnlyCollection<string>? additions)
    {
        if (additions is null || additions.Count == 0)
        {
            return;
        }

        HashSet<string> ids = ReadProjectlessThreadIds(root);
        ids.UnionWith(additions.Where(id => !string.IsNullOrWhiteSpace(id)));
        root["projectless-thread-ids"] = new JsonArray(
            ids.Order(StringComparer.Ordinal).Select(id => JsonValue.Create(id)).ToArray());
    }

    private static IReadOnlyList<string> ReadWorkspaceRoots(JsonObject root)
    {
        return CodexPathService.NormalizeDistinctPaths(
            ReadPaths(root["electron-saved-workspace-roots"])
                .Concat(ReadPaths(root["project-order"]))
                .Concat(ReadPaths(root["active-workspace-roots"])));
    }

    private static HashSet<string> ReadProjectlessThreadIds(JsonObject root)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (root["projectless-thread-ids"] is not JsonArray items)
        {
            return ids;
        }

        foreach (JsonNode? item in items)
        {
            try
            {
                string? id = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
            catch
            {
            }
        }

        return ids;
    }

    private static void NormalizePathArray(JsonObject root, string key)
    {
        if (root[key] is null)
        {
            return;
        }

        IReadOnlyList<string> paths = ReadPaths(root[key]);
        root[key] = new JsonArray(CodexPathService.NormalizeDistinctPaths(paths)
            .Select(path => JsonValue.Create(path))
            .ToArray());
    }

    private static void NormalizeActiveWorkspaceRoots(JsonObject root)
    {
        JsonNode? current = root["active-workspace-roots"];
        if (current is null)
        {
            return;
        }

        IReadOnlyList<string> paths = CodexPathService.NormalizeDistinctPaths(ReadPaths(current));
        if (current is JsonArray)
        {
            root["active-workspace-roots"] = new JsonArray(paths.Select(path => JsonValue.Create(path)).ToArray());
        }
        else if (paths.Count > 0)
        {
            root["active-workspace-roots"] = paths[0];
        }
    }

    private static void NormalizeObjectKeys(JsonObject root, string key)
    {
        if (root[key] is not JsonObject current)
        {
            return;
        }

        JsonObject normalized = [];
        foreach ((string path, JsonNode? value) in current)
        {
            normalized[CodexPathService.ToDesktopPath(path)] = value?.DeepClone();
        }

        root[key] = normalized;
    }

    private static void NormalizeOpenTargetPreferences(JsonObject root)
    {
        if (root["open-in-target-preferences"] is not JsonObject preferences ||
            preferences["perPath"] is not JsonObject perPath)
        {
            return;
        }

        JsonObject normalized = [];
        foreach ((string path, JsonNode? value) in perPath)
        {
            normalized[CodexPathService.ToDesktopPath(path)] = value?.DeepClone();
        }

        preferences["perPath"] = normalized;
    }

    private static IReadOnlyList<string> ReadPaths(JsonNode? node)
    {
        List<string> paths = [];
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                try
                {
                    string? value = item?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        paths.Add(value);
                    }
                }
                catch
                {
                }
            }
        }
        else
        {
            try
            {
                string? value = node?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    paths.Add(value);
                }
            }
            catch
            {
            }
        }

        return paths;
    }
}
