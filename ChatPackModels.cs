namespace CodexSessionHotSync;

internal sealed record ChatPackManifest
{
    public int FormatVersion { get; init; } = 1;
    public string CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public string SourceAppVersion { get; init; } = string.Empty;
    public List<ChatPackSessionEntry> Sessions { get; init; } = [];
    public List<ChatPackAttachmentEntry> Attachments { get; init; } = [];
}

internal sealed record ChatPackSessionEntry
{
    public required string Id { get; init; }
    public required string ArchiveEntry { get; init; }
    public required string SourceRelativePath { get; init; }
    public required string OriginalProjectPath { get; init; }
    public required bool Projectless { get; init; }
    public required bool Archived { get; init; }
    public required string Title { get; init; }
    public required string UpdatedAt { get; init; }
}

internal sealed record ChatPackAttachmentEntry
{
    public required string ArchiveEntry { get; init; }
    public required string SourceRelativePath { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
    public List<string> SourceReferences { get; init; } = [];
    public List<string> SessionIds { get; init; } = [];
}

internal sealed record ChatPackProjectMapping
{
    public required string SourcePath { get; init; }
    public required string ProjectName { get; init; }
    public required int SessionCount { get; init; }
    public required bool RequiresPathMapping { get; init; }
    public string TargetPath { get; set; } = string.Empty;
    public bool ImportSessions { get; set; } = true;
}

internal sealed record ChatPackExportProject
{
    public required string SourcePath { get; init; }
    public required string ProjectName { get; init; }
    public required int SessionCount { get; init; }
    public required IReadOnlySet<string> SessionIds { get; init; }
    public bool ExportSessions { get; set; } = true;
}

internal sealed record ChatPackExportPreview
{
    public required string CodexHome { get; init; }
    public required bool IncludeArchived { get; init; }
    public required IReadOnlyList<ChatPackExportProject> Projects { get; init; }
}

internal sealed record ChatPackPreview
{
    public required string PackagePath { get; init; }
    public required ChatPackManifest Manifest { get; init; }
    public required IReadOnlyList<ChatPackProjectMapping> Mappings { get; init; }
}

internal sealed record ChatPackExportResult
{
    public required string PackagePath { get; init; }
    public required int SessionCount { get; init; }
    public required int ProjectCount { get; init; }
    public required int AttachmentCount { get; init; }
}

internal sealed record ChatPackImportResult
{
    public required int ImportedSessions { get; init; }
    public required int ImportedAttachments { get; init; }
    public required int SkippedExistingSessions { get; init; }
    public required int ExcludedSessions { get; init; }
    public required IReadOnlyList<string> AddedPaths { get; init; }
    public required IReadOnlyDictionary<string, string> Titles { get; init; }
    public required IReadOnlyList<string> WorkspaceRoots { get; init; }
    public required IReadOnlySet<string> ProjectlessThreadIds { get; init; }
}

internal sealed record SessionSyncOverrides
{
    public IReadOnlyDictionary<string, string> PreferredTitles { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> WorkspaceRoots { get; init; } = [];
    public IReadOnlySet<string> ProjectlessThreadIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}
