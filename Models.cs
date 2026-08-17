using System.Text.Json.Nodes;

namespace CodexSessionHotSync;

internal sealed record SessionRecord
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string RelativePath { get; init; }
    public required string DirectoryName { get; init; }
    public required bool Archived { get; init; }
    public required string ModelProvider { get; init; }
    public required string Cwd { get; init; }
    public required string Source { get; init; }
    public required string CliVersion { get; init; }
    public required string HistoryMode { get; init; }
    public string? ThreadSource { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required long Length { get; init; }
}

internal sealed record SessionScanResult
{
    public required IReadOnlyList<SessionRecord> Sessions { get; init; }
    public required IReadOnlyList<string> UnreadableFiles { get; init; }
    public required IReadOnlyList<string> InvalidFiles { get; init; }
    public required IReadOnlyDictionary<string, int> ProviderCounts { get; init; }
}

internal sealed record DatabaseLocation(string Key, string Label, string Path, string RelativePath);

internal sealed record DatabaseStatus
{
    public required DatabaseLocation Location { get; init; }
    public required bool Exists { get; init; }
    public required bool Readable { get; init; }
    public string? Error { get; init; }
    public int TotalRows { get; init; }
    public int ValidRows { get; init; }
    public int MissingJsonlRows { get; init; }
    public int MissingFromDatabase { get; init; }
    public int WrongProviderRows { get; init; }
    public int WrongRolloutPathRows { get; init; }
}

internal sealed record InspectionSnapshot
{
    public required string CodexHome { get; init; }
    public required string CurrentProvider { get; init; }
    public required IReadOnlyList<string> ProviderOptions { get; init; }
    public required SessionScanResult Scan { get; init; }
    public required IReadOnlyList<SessionRecord> CanonicalSessions { get; init; }
    public required IReadOnlyList<DatabaseStatus> Databases { get; init; }
    public required int IndexEntryCount { get; init; }
    public required int MissingIndexEntries { get; init; }
    public required bool GlobalStateNeedsNormalization { get; init; }
    public int DuplicateJsonlCount => Scan.Sessions.Count - CanonicalSessions.Count;
    public int TotalIssues => Databases.Sum(item =>
        item.MissingFromDatabase + item.WrongProviderRows + item.WrongRolloutPathRows) +
        MissingIndexEntries +
        DuplicateJsonlCount +
        (GlobalStateNeedsNormalization ? 1 : 0);
}

internal sealed record JsonlProviderUpdateResult
{
    public required IReadOnlyList<string> ChangedPaths { get; init; }
    public required IReadOnlyList<string> SkippedPaths { get; init; }
}

internal sealed record DatabaseSyncStats
{
    public required string Label { get; init; }
    public int InsertedRows { get; init; }
    public int UpdatedRows { get; init; }
    public int SkippedOrphans { get; init; }
    public int RepairedOrphans { get; init; }
}

internal sealed record SessionIndexUpdateResult(int Entries, int Added, int Deduplicated, bool Changed);

internal sealed record SyncResult
{
    public required string BackupDirectory { get; init; }
    public required string TargetProvider { get; init; }
    public required int ValidSessions { get; init; }
    public required int DuplicateJsonlFiles { get; init; }
    public required JsonlProviderUpdateResult Jsonl { get; init; }
    public required IReadOnlyList<DatabaseSyncStats> Databases { get; init; }
    public required SessionIndexUpdateResult SessionIndex { get; init; }
    public required bool GlobalStateUpdated { get; init; }
    public required TimeSpan Duration { get; init; }
}

internal sealed record PreparedIndexFile
{
    public required string Path { get; init; }
    public required byte[] Contents { get; init; }
    public required SessionIndexUpdateResult Result { get; init; }
}

internal sealed record ThreadRow(Dictionary<string, object?> Values)
{
    public string Id => Convert.ToString(Values.GetValueOrDefault("id")) ?? string.Empty;

    public long SortTimestamp
    {
        get
        {
            foreach (string key in new[] { "updated_at_ms", "recency_at_ms", "updated_at", "recency_at", "created_at_ms", "created_at" })
            {
                if (Values.TryGetValue(key, out object? value) && value is not null && value is not DBNull)
                {
                    try
                    {
                        return Convert.ToInt64(value);
                    }
                    catch
                    {
                    }
                }
            }

            return 0;
        }
    }
}

internal sealed record SessionIndexEntry(string Id, JsonObject Data, int OriginalOrder);

internal sealed record BackupManifest
{
    public required string CreatedAt { get; init; }
    public required string CodexHome { get; init; }
    public required string TargetProvider { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
}
