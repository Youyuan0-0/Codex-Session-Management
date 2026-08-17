using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSessionHotSync;

internal sealed class ChatPackService
{
    private const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
    };
    private readonly JsonlSessionService _jsonl = new();
    private readonly SqliteSessionService _sqlite = new();
    private readonly SessionIndexService _index = new();
    private readonly CodexGlobalStateService _globalState = new();

    public async Task<ChatPackExportResult> ExportAsync(
        string requestedCodexHome,
        bool includeArchived,
        string destinationPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? selectedSessionIds = null)
    {
        string codexHome = CodexConfigService.NormalizeCodexHome(requestedCodexHome);
        string destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        progress?.Report("正在扫描可导出的聊天记录…");
        SessionScanResult scan = await _jsonl.ScanAsync(codexHome, includeArchived, cancellationToken);
        IReadOnlyList<SessionRecord> sessions = JsonlSessionService.SelectCanonicalSessions(scan.Sessions);
        if (selectedSessionIds is not null)
        {
            sessions = sessions
                .Where(item => selectedSessionIds.Contains(item.Id))
                .ToArray();
        }

        if (sessions.Count == 0)
        {
            throw new InvalidOperationException(
                selectedSessionIds is null
                    ? "没有找到可以导出的聊天记录。"
                    : "所选项目中没有找到可以导出的聊天记录。");
        }

        IReadOnlyList<DatabaseLocation> locations = await _sqlite.DiscoverLocationsAsync(codexHome, cancellationToken);
        IReadOnlyDictionary<string, string> databaseTitles = await _sqlite.ReadTitlesAsync(locations, cancellationToken);
        var (_, indexTitles) = await _index.ReadSummaryAsync(codexHome, cancellationToken);
        Dictionary<string, string> titles = new(databaseTitles, StringComparer.Ordinal);
        foreach ((string id, string title) in indexTitles)
        {
            titles[id] = title;
        }

        PreparedGlobalStateFile state = await _globalState.PrepareAsync(codexHome, cancellationToken: cancellationToken);
        string attachmentsRoot = Path.Combine(codexHome, "attachments");
        IReadOnlyList<ManagedAttachmentFile> availableAttachments = EnumerateManagedAttachments(attachmentsRoot);
        Dictionary<string, PendingAttachment> referencedAttachments = new(StringComparer.OrdinalIgnoreCase);
        ChatPackManifest manifest = new()
        {
            FormatVersion = CurrentFormatVersion,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            SourceAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        };
        string tempPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                int completed = 0;
                foreach (SessionRecord session in sessions.OrderBy(item => item.UpdatedAt))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DiscoverReferencedAttachmentsAsync(
                        session.FilePath,
                        session.Id,
                        attachmentsRoot,
                        availableAttachments,
                        referencedAttachments,
                        cancellationToken);
                    string entryName = $"conversations/{session.Id}.jsonl";
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
                    await using (Stream destinationStream = entry.Open())
                    await using (FileStream sourceStream = new(
                                     session.FilePath,
                                     FileMode.Open,
                                     FileAccess.Read,
                                     FileShare.ReadWrite | FileShare.Delete,
                                     65536,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
                    }

                    string title = titles.GetValueOrDefault(session.Id) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = "会话 " + session.Id[..Math.Min(8, session.Id.Length)];
                    }

                    manifest.Sessions.Add(new ChatPackSessionEntry
                    {
                        Id = session.Id,
                        ArchiveEntry = entryName,
                        SourceRelativePath = session.RelativePath.Replace('\\', '/'),
                        OriginalProjectPath = CodexPathService.ToDesktopPath(session.Cwd),
                        Projectless = state.ProjectlessThreadIds.Contains(session.Id) || string.IsNullOrWhiteSpace(session.Cwd),
                        Archived = session.Archived,
                        Title = title,
                        UpdatedAt = session.UpdatedAt.UtcDateTime.ToString("O"),
                    });
                    completed++;
                    if (completed == sessions.Count || completed % 20 == 0)
                    {
                        progress?.Report($"正在导出聊天记录：{completed:N0}/{sessions.Count:N0}");
                    }
                }

                int attachmentIndex = 0;
                foreach (PendingAttachment attachment in referencedAttachments.Values
                             .OrderBy(item => item.File.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string archiveEntryName = "attachments/" + attachment.File.RelativePath.Replace('\\', '/');
                    ZipArchiveEntry attachmentEntry = archive.CreateEntry(
                        archiveEntryName,
                        CompressionLevel.SmallestSize);
                    (long length, string sha256) = await CopyAttachmentToArchiveAsync(
                        attachment.File.FullPath,
                        attachmentEntry,
                        cancellationToken);
                    manifest.Attachments.Add(new ChatPackAttachmentEntry
                    {
                        ArchiveEntry = archiveEntryName,
                        SourceRelativePath = attachment.File.RelativePath.Replace('\\', '/'),
                        Length = length,
                        Sha256 = sha256,
                        SourceReferences = attachment.SourceReferences
                            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        SessionIds = attachment.SessionIds
                            .OrderBy(item => item, StringComparer.Ordinal)
                            .ToList(),
                    });
                    attachmentIndex++;
                    progress?.Report(
                        $"正在压缩引用附件：{attachmentIndex:N0}/{referencedAttachments.Count:N0}");
                }

                ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
                await using Stream manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, ManifestJson, cancellationToken);
            }

            File.Move(tempPath, destination, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        int projects = manifest.Sessions
            .Select(item => ComparisonPath(item.OriginalProjectPath))
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new ChatPackExportResult
        {
            PackagePath = destination,
            SessionCount = manifest.Sessions.Count,
            ProjectCount = projects,
            AttachmentCount = manifest.Attachments.Count,
        };
    }

    public async Task<ChatPackExportPreview> ReadExportPreviewAsync(
        string requestedCodexHome,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        string codexHome = CodexConfigService.NormalizeCodexHome(requestedCodexHome);
        SessionScanResult scan = await _jsonl.ScanAsync(codexHome, includeArchived, cancellationToken);
        IReadOnlyList<SessionRecord> sessions = JsonlSessionService.SelectCanonicalSessions(scan.Sessions);
        if (sessions.Count == 0)
        {
            throw new InvalidOperationException("没有找到可以导出的聊天记录。");
        }

        PreparedGlobalStateFile state = await _globalState.PrepareAsync(
            codexHome,
            cancellationToken: cancellationToken);
        List<ChatPackExportProject> projects = sessions
            .GroupBy(item => ComparisonPath(item.Cwd), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                SessionRecord sample = group.First();
                string source = group.Key.Length > 0
                    ? CodexPathService.ToDesktopPath(sample.Cwd)
                    : string.Empty;
                int projectlessSessions = group.Count(item =>
                    state.ProjectlessThreadIds.Contains(item.Id) || string.IsNullOrWhiteSpace(item.Cwd));
                return new ChatPackExportProject
                {
                    SourcePath = source,
                    ProjectName = ProjectDisplayName(source, projectlessSessions, group.Count()),
                    SessionCount = group.Count(),
                    SessionIds = group
                        .Select(item => item.Id)
                        .ToHashSet(StringComparer.Ordinal),
                };
            })
            .OrderByDescending(item => item.SourcePath.Length > 0)
            .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ChatPackExportPreview
        {
            CodexHome = codexHome,
            IncludeArchived = includeArchived,
            Projects = projects,
        };
    }

    public async Task<ChatPackPreview> ReadPreviewAsync(
        string packagePath,
        string requestedCodexHome,
        CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(packagePath);
        ChatPackManifest manifest = await ReadManifestAsync(path, cancellationToken);
        string codexHome = CodexConfigService.NormalizeCodexHome(requestedCodexHome);
        IReadOnlyList<string> localRoots = await ReadLocalWorkspaceRootsAsync(codexHome, cancellationToken);
        List<ChatPackProjectMapping> mappings = manifest.Sessions
            .GroupBy(
                item => ComparisonPath(item.OriginalProjectPath),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                ChatPackSessionEntry sample = group.First();
                bool requiresPathMapping = group.Key.Length > 0;
                string source = requiresPathMapping
                    ? CodexPathService.ToDesktopPath(sample.OriginalProjectPath)
                    : string.Empty;
                int projectlessSessions = group.Count(item => item.Projectless);

                return new ChatPackProjectMapping
                {
                    SourcePath = source,
                    ProjectName = ProjectDisplayName(source, projectlessSessions, group.Count()),
                    SessionCount = group.Count(),
                    RequiresPathMapping = requiresPathMapping,
                    TargetPath = requiresPathMapping ? ResolveLocalProjectPath(source, localRoots) : string.Empty,
                };
            })
            .OrderByDescending(item => item.RequiresPathMapping)
            .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ChatPackPreview
        {
            PackagePath = path,
            Manifest = manifest,
            Mappings = mappings,
        };
    }

    public async Task<ChatPackImportResult> ImportAsync(
        ChatPackPreview preview,
        string requestedCodexHome,
        IReadOnlyList<ChatPackProjectMapping> mappings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string codexHome = CodexConfigService.NormalizeCodexHome(requestedCodexHome);
        Directory.CreateDirectory(codexHome);
        Dictionary<string, ChatPackProjectMapping> mappingBySource = mappings
            .Where(item => item.ImportSessions && item.RequiresPathMapping)
            .ToDictionary(item => ComparisonPath(item.SourcePath), StringComparer.OrdinalIgnoreCase);
        foreach (ChatPackProjectMapping mapping in mappings.Where(
                     item => item.ImportSessions && item.RequiresPathMapping))
        {
            if (string.IsNullOrWhiteSpace(mapping.TargetPath) || !Directory.Exists(mapping.TargetPath))
            {
                throw new DirectoryNotFoundException($"项目 {mapping.ProjectName} 的本地映射路径不存在：{mapping.TargetPath}");
            }
        }

        SessionScanResult existingScan = await _jsonl.ScanAsync(codexHome, true, cancellationToken);
        HashSet<string> existingIds = existingScan.Sessions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        bool importPathless = mappings.Any(item => !item.RequiresPathMapping && item.ImportSessions);
        IReadOnlyList<ChatPackSessionEntry> selectedSessions = preview.Manifest.Sessions
            .Where(session => ComparisonPath(session.OriginalProjectPath) is { Length: > 0 } sourceKey
                ? mappingBySource.ContainsKey(sourceKey)
                : importPathless)
            .ToArray();
        HashSet<string> newSessionIds = selectedSessions
            .Select(item => item.Id)
            .Where(id => !existingIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        List<string> addedPaths = [];
        Dictionary<string, string> titles = new(StringComparer.Ordinal);
        HashSet<string> projectlessIds = new(StringComparer.Ordinal);
        List<string> workspaceRoots = mappings
            .Where(item => item.ImportSessions && item.RequiresPathMapping)
            .Select(item => Path.GetFullPath(item.TargetPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        int skipped = selectedSessions.Count - newSessionIds.Count;
        int excluded = preview.Manifest.Sessions.Count - selectedSessions.Count;
        int importedSessions = 0;
        int importedAttachments = 0;
        try
        {
            await using FileStream input = new(preview.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false, Encoding.UTF8);
            IReadOnlyDictionary<string, string> attachmentPathReplacements =
                await ImportReferencedAttachmentsAsync(
                archive,
                preview.Manifest.Attachments,
                newSessionIds,
                codexHome,
                addedPaths,
                progress,
                cancellationToken);
            importedAttachments = addedPaths.Count;
            int completed = 0;
            foreach (ChatPackSessionEntry session in selectedSessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!newSessionIds.Contains(session.Id))
                {
                    continue;
                }

                ZipArchiveEntry entry = archive.GetEntry(session.ArchiveEntry)
                    ?? throw new InvalidDataException($"聊天包缺少会话文件：{session.ArchiveEntry}");
                string? mappedProject = null;
                string sourceKey = ComparisonPath(session.OriginalProjectPath);
                if (sourceKey.Length > 0)
                {
                    if (!mappingBySource.TryGetValue(sourceKey, out ChatPackProjectMapping? mapping))
                    {
                        throw new InvalidDataException($"缺少项目路径映射：{session.OriginalProjectPath}");
                    }

                    mappedProject = Path.GetFullPath(mapping.TargetPath);
                }

                if (session.Projectless)
                {
                    projectlessIds.Add(session.Id);
                }

                string destination = ImportDestination(codexHome, session);
                string tempPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try
                {
                    await using Stream source = entry.Open();
                    await RewriteImportedSessionAsync(
                        source,
                        tempPath,
                        session.Id,
                        mappedProject,
                        attachmentPathReplacements,
                        cancellationToken);
                    File.Move(tempPath, destination, false);
                    addedPaths.Add(destination);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }

                if (!string.IsNullOrWhiteSpace(session.Title))
                {
                    titles[session.Id] = session.Title;
                }

                completed++;
                importedSessions++;
                if (completed == newSessionIds.Count || completed % 20 == 0)
                {
                    progress?.Report($"正在导入聊天记录：{completed:N0}/{newSessionIds.Count:N0}");
                }
            }
        }
        catch
        {
            await RollbackImportAsync(addedPaths);
            throw;
        }

        return new ChatPackImportResult
        {
            ImportedSessions = importedSessions,
            ImportedAttachments = importedAttachments,
            SkippedExistingSessions = skipped,
            ExcludedSessions = excluded,
            AddedPaths = addedPaths,
            Titles = titles,
            WorkspaceRoots = workspaceRoots,
            ProjectlessThreadIds = projectlessIds,
        };
    }

    public Task RollbackImportAsync(IEnumerable<string> addedPaths)
    {
        foreach (string path in addedPaths.Reverse())
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<string>> ReadLocalWorkspaceRootsAsync(
        string codexHome,
        CancellationToken cancellationToken)
    {
        List<string> candidates = [];
        PreparedGlobalStateFile state = await _globalState.PrepareAsync(codexHome, cancellationToken: cancellationToken);
        candidates.AddRange(state.WorkspaceRoots);
        try
        {
            SessionScanResult scan = await _jsonl.ScanAsync(codexHome, true, cancellationToken);
            candidates.AddRange(scan.Sessions.Select(item => item.Cwd));
        }
        catch
        {
        }

        return CodexPathService.NormalizeDistinctPaths(candidates)
            .Where(Directory.Exists)
            .ToArray();
    }

    private static async Task<ChatPackManifest> ReadManifestAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("聊天包不存在。", packagePath);
        }

        await using FileStream input = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(input, ZipArchiveMode.Read, false, Encoding.UTF8);
        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("不是有效的 Codex 聊天包：缺少 manifest.json。");
        if (manifestEntry.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("聊天包清单异常过大。");
        }

        await using Stream manifestStream = manifestEntry.Open();
        ChatPackManifest manifest = await JsonSerializer.DeserializeAsync<ChatPackManifest>(
            manifestStream,
            ManifestJson,
            cancellationToken) ?? throw new InvalidDataException("聊天包清单为空。");
        ValidateManifest(manifest, archive);
        return manifest;
    }

    private static void ValidateManifest(ChatPackManifest manifest, ZipArchive archive)
    {
        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"不支持的聊天包版本：{manifest.FormatVersion}。");
        }

        if (manifest.Sessions is null || manifest.Sessions.Count == 0)
        {
            throw new InvalidDataException("聊天包中没有聊天记录。");
        }

        if (manifest.Attachments is null)
        {
            throw new InvalidDataException("聊天包附件清单无效。");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ChatPackSessionEntry session in manifest.Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Id) || !ids.Add(session.Id))
            {
                throw new InvalidDataException("聊天包包含空会话 ID 或重复会话 ID。");
            }

            string expected = $"conversations/{session.Id}.jsonl";
            if (!string.Equals(session.ArchiveEntry, expected, StringComparison.Ordinal) || archive.GetEntry(expected) is null)
            {
                throw new InvalidDataException($"聊天包会话文件无效：{session.Id}。");
            }
        }

        HashSet<string> attachmentPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> attachmentReferences = new(StringComparer.OrdinalIgnoreCase);
        foreach (ChatPackAttachmentEntry attachment in manifest.Attachments)
        {
            if (!IsSafeRelativePath(attachment.SourceRelativePath) ||
                !attachmentPaths.Add(attachment.SourceRelativePath))
            {
                throw new InvalidDataException("聊天包包含无效或重复的附件路径。");
            }

            string expected = "attachments/" + attachment.SourceRelativePath.Replace('\\', '/');
            ZipArchiveEntry? entry = archive.GetEntry(expected);
            if (!string.Equals(attachment.ArchiveEntry, expected, StringComparison.Ordinal) ||
                entry is null ||
                attachment.Length < 0 ||
                entry.Length != attachment.Length ||
                !IsSha256(attachment.Sha256))
            {
                throw new InvalidDataException($"聊天包附件文件无效：{attachment.SourceRelativePath}。");
            }

            if (attachment.SourceReferences is null || attachment.SourceReferences.Count == 0 ||
                attachment.SessionIds is null || attachment.SessionIds.Count == 0)
            {
                throw new InvalidDataException($"聊天包附件引用无效：{attachment.SourceRelativePath}。");
            }

            foreach (string sourceReference in attachment.SourceReferences)
            {
                if (!IsManagedAttachmentReference(sourceReference, attachment.SourceRelativePath) ||
                    !attachmentReferences.Add(sourceReference))
                {
                    throw new InvalidDataException($"聊天包包含无效或重复的附件引用：{attachment.SourceRelativePath}。");
                }
            }

            if (attachment.SessionIds.Any(id => !ids.Contains(id)))
            {
                throw new InvalidDataException($"聊天包附件关联了不存在的会话：{attachment.SourceRelativePath}。");
            }
        }
    }

    private static async Task RewriteImportedSessionAsync(
        Stream source,
        string destination,
        string expectedId,
        string? mappedProject,
        IReadOnlyDictionary<string, string> attachmentPathReplacements,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(source, Encoding.UTF8, true, 65536, true);
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using StreamWriter writer = new(output, new UTF8Encoding(false), 65536, true)
        {
            NewLine = Environment.NewLine,
        };
        bool metadataFound = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            string outputLine = line;
            bool shouldInspect = line.Contains("session_meta", StringComparison.Ordinal) ||
                                 (attachmentPathReplacements.Count > 0 &&
                                  line.Contains("attachments", StringComparison.OrdinalIgnoreCase));
            if (shouldInspect)
            {
                try
                {
                    if (JsonNode.Parse(line) is JsonObject record)
                    {
                        bool changed = RewriteAttachmentReferences(record, attachmentPathReplacements);
                        if (string.Equals(record["type"]?.GetValue<string>(), "session_meta", StringComparison.Ordinal) &&
                            record["payload"] is JsonObject payload &&
                            string.Equals(payload["id"]?.GetValue<string>(), expectedId, StringComparison.Ordinal))
                        {
                            metadataFound = true;
                            if (!string.IsNullOrWhiteSpace(mappedProject) &&
                                !string.Equals(
                                    payload["cwd"]?.GetValue<string>(),
                                    mappedProject,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                payload["cwd"] = mappedProject;
                                changed = true;
                            }
                        }

                        if (changed)
                        {
                            outputLine = record.ToJsonString(CompactJson);
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }

            await writer.WriteLineAsync(outputLine.AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        await output.FlushAsync(cancellationToken);
        if (!metadataFound)
        {
            throw new InvalidDataException($"会话 {expectedId} 缺少有效 session_meta。");
        }
    }

    private static IReadOnlyList<ManagedAttachmentFile> EnumerateManagedAttachments(string attachmentsRoot)
    {
        if (!Directory.Exists(attachmentsRoot))
        {
            return [];
        }

        string fullRoot = Path.GetFullPath(attachmentsRoot);
        List<ManagedAttachmentFile> files = [];
        foreach (string filePath in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(fullRoot, filePath);
            if (!IsSafeRelativePath(relativePath) ||
                string.Equals(relativePath, "pasted-text-attachments.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(filePath);
            files.Add(new ManagedAttachmentFile(
                fullPath,
                relativePath,
                ComparisonPath(fullPath)));
        }

        return files
            .OrderByDescending(item => item.ComparisonFullPath.Length)
            .ToArray();
    }

    private static async Task DiscoverReferencedAttachmentsAsync(
        string sessionPath,
        string sessionId,
        string attachmentsRoot,
        IReadOnlyList<ManagedAttachmentFile> availableAttachments,
        IDictionary<string, PendingAttachment> referencedAttachments,
        CancellationToken cancellationToken)
    {
        if (availableAttachments.Count == 0)
        {
            return;
        }

        string comparisonRoot = ComparisonPath(attachmentsRoot) + "\\";
        await using FileStream input = new(
            sessionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(input, Encoding.UTF8, true, 65536, true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.Contains("attachments", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonNode? record;
            try
            {
                record = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (string value in EnumerateStringValues(record))
            {
                string comparisonValue = value.Replace('/', '\\');
                if (!comparisonValue.Contains(comparisonRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (ManagedAttachmentFile file in availableAttachments)
                {
                    int startIndex = 0;
                    while ((startIndex = comparisonValue.IndexOf(
                               file.ComparisonFullPath,
                               startIndex,
                               StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        int referenceStart = startIndex;
                        int referenceLength = file.ComparisonFullPath.Length;
                        if (referenceStart >= 4 &&
                            string.Equals(
                                comparisonValue.Substring(referenceStart - 4, 4),
                                "\\\\?\\",
                                StringComparison.Ordinal))
                        {
                            referenceStart -= 4;
                            referenceLength += 4;
                        }

                        string sourceReference = value.Substring(referenceStart, referenceLength);
                        if (!referencedAttachments.TryGetValue(file.RelativePath, out PendingAttachment? pending))
                        {
                            pending = new PendingAttachment(file);
                            referencedAttachments[file.RelativePath] = pending;
                        }

                        pending.SourceReferences.Add(sourceReference);
                        pending.SessionIds.Add(sessionId);
                        startIndex += file.ComparisonFullPath.Length;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateStringValues(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue(out string? text) && text is not null:
                yield return text;
                break;
            case JsonObject jsonObject:
                foreach ((_, JsonNode? child) in jsonObject)
                {
                    foreach (string textValue in EnumerateStringValues(child))
                    {
                        yield return textValue;
                    }
                }

                break;
            case JsonArray jsonArray:
                foreach (JsonNode? child in jsonArray)
                {
                    foreach (string textValue in EnumerateStringValues(child))
                    {
                        yield return textValue;
                    }
                }

                break;
        }
    }

    private static async Task<(long Length, string Sha256)> CopyAttachmentToArchiveAsync(
        string sourcePath,
        ZipArchiveEntry destinationEntry,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using Stream destination = destinationEntry.Open();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<IReadOnlyDictionary<string, string>> ImportReferencedAttachmentsAsync(
        ZipArchive archive,
        IReadOnlyList<ChatPackAttachmentEntry> attachments,
        IReadOnlySet<string> importedSessionIds,
        string codexHome,
        List<string> addedPaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> replacements = new(StringComparer.OrdinalIgnoreCase);
        if (importedSessionIds.Count == 0 || attachments.Count == 0)
        {
            return replacements;
        }

        ChatPackAttachmentEntry[] requiredAttachments = attachments
            .Where(item => item.SessionIds.Count == 0 || item.SessionIds.Any(importedSessionIds.Contains))
            .OrderBy(item => item.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string attachmentsRoot = Path.Combine(codexHome, "attachments");
        int completed = 0;
        foreach (ChatPackAttachmentEntry attachment in requiredAttachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.GetEntry(attachment.ArchiveEntry)
                ?? throw new InvalidDataException($"聊天包缺少附件：{attachment.SourceRelativePath}");
            string preferredDestination = SafeAttachmentDestination(
                attachmentsRoot,
                attachment.SourceRelativePath);
            string destination = ResolveAttachmentDestination(preferredDestination, attachment.Sha256);
            if (!File.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string tempPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await CopyAndVerifyAttachmentAsync(entry, tempPath, attachment, cancellationToken);
                    File.Move(tempPath, destination, false);
                    addedPaths.Add(destination);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }

            string desktopDestination = CodexPathService.ToDesktopPath(destination);
            foreach (string sourceReference in attachment.SourceReferences)
            {
                replacements[sourceReference] = desktopDestination;
            }

            completed++;
            progress?.Report($"正在还原引用附件：{completed:N0}/{requiredAttachments.Length:N0}");
        }

        return replacements;
    }

    private static string ResolveAttachmentDestination(string preferredPath, string expectedSha256)
    {
        if (File.Exists(preferredPath) && FileHasSha256(preferredPath, expectedSha256))
        {
            return preferredPath;
        }

        if (!File.Exists(preferredPath) && !Directory.Exists(preferredPath))
        {
            return preferredPath;
        }

        string directory = Path.GetDirectoryName(preferredPath)!;
        string name = Path.GetFileNameWithoutExtension(preferredPath);
        string extension = Path.GetExtension(preferredPath);
        string hashSuffix = expectedSha256[..8].ToLowerInvariant();
        for (int suffix = 0; ; suffix++)
        {
            string suffixText = suffix == 0 ? string.Empty : $"-{suffix}";
            string candidate = Path.Combine(directory, $"{name}-imported-{hashSuffix}{suffixText}{extension}");
            if (File.Exists(candidate) && FileHasSha256(candidate, expectedSha256))
            {
                return candidate;
            }

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task CopyAndVerifyAttachmentAsync(
        ZipArchiveEntry sourceEntry,
        string destination,
        ChatPackAttachmentEntry attachment,
        CancellationToken cancellationToken)
    {
        await using Stream source = sourceEntry.Open();
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        await output.FlushAsync(cancellationToken);
        string actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (length != attachment.Length ||
            !string.Equals(actualSha256, attachment.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"聊天包附件校验失败：{attachment.SourceRelativePath}。");
        }
    }

    private static bool RewriteAttachmentReferences(
        JsonNode? node,
        IReadOnlyDictionary<string, string> replacements)
    {
        bool changed = false;
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (string key in jsonObject.Select(item => item.Key).ToArray())
                {
                    JsonNode? child = jsonObject[key];
                    if (TryRewriteStringValue(child, replacements, out string? rewritten))
                    {
                        jsonObject[key] = rewritten;
                        changed = true;
                    }
                    else
                    {
                        changed |= RewriteAttachmentReferences(child, replacements);
                    }
                }

                break;
            case JsonArray jsonArray:
                for (int index = 0; index < jsonArray.Count; index++)
                {
                    JsonNode? child = jsonArray[index];
                    if (TryRewriteStringValue(child, replacements, out string? rewritten))
                    {
                        jsonArray[index] = rewritten;
                        changed = true;
                    }
                    else
                    {
                        changed |= RewriteAttachmentReferences(child, replacements);
                    }
                }

                break;
        }

        return changed;
    }

    private static bool TryRewriteStringValue(
        JsonNode? node,
        IReadOnlyDictionary<string, string> replacements,
        out string? rewritten)
    {
        rewritten = null;
        if (node is not JsonValue value || !value.TryGetValue(out string? original) || original is null)
        {
            return false;
        }

        string result = original;
        foreach ((string source, string destination) in replacements.OrderByDescending(item => item.Key.Length))
        {
            result = result.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(result, original, StringComparison.Ordinal))
        {
            return false;
        }

        rewritten = result;
        return true;
    }

    private static string SafeAttachmentDestination(string attachmentsRoot, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new InvalidDataException($"聊天包附件路径无效：{relativePath}。");
        }

        string root = Path.GetFullPath(attachmentsRoot);
        string destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', '\\')));
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"聊天包附件路径越界：{relativePath}。");
        }

        return destination;
    }

    private static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        string[] segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(item => item is "." or ".." || item.Contains(':')))
        {
            return false;
        }

        try
        {
            string safetyRoot = Path.Combine(Path.GetTempPath(), "CodexChatPackPathCheck");
            string rootPrefix = Path.GetFullPath(safetyRoot).TrimEnd(Path.DirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(safetyRoot, relativePath.Replace('/', '\\')));
            return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsManagedAttachmentReference(string? sourceReference, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(sourceReference))
        {
            return false;
        }

        string normalized = sourceReference.Replace('/', '\\');
        string suffix = "\\attachments\\" + relativePath.Replace('/', '\\');
        return Path.IsPathFullyQualified(normalized) &&
               normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool FileHasSha256(string path, string expectedSha256)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string actual = Convert.ToHexString(SHA256.HashData(input));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ImportDestination(string codexHome, ChatPackSessionEntry session)
    {
        DateTimeOffset timestamp = DateTimeOffset.TryParse(session.UpdatedAt, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        string root = Path.Combine(codexHome, session.Archived ? "archived_sessions" : "sessions");
        string directory = Path.Combine(root, timestamp.ToString("yyyy"), timestamp.ToString("MM"), timestamp.ToString("dd"));
        string baseName = $"rollout-import-{timestamp:yyyy-MM-ddTHH-mm-ss}-{session.Id}.jsonl";
        string path = Path.Combine(directory, baseName);
        for (int suffix = 1; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, Path.GetFileNameWithoutExtension(baseName) + $"-{suffix}.jsonl");
        }

        return path;
    }

    private static string ResolveLocalProjectPath(string sourcePath, IReadOnlyList<string> localRoots)
    {
        if (Directory.Exists(sourcePath))
        {
            return Path.GetFullPath(sourcePath);
        }

        string projectName = ProjectName(sourcePath);
        string? match = localRoots
            .Where(Directory.Exists)
            .Select(path => new { Path = path, Score = PathMatchScore(sourcePath, path, projectName) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .Select(item => item.Path)
            .FirstOrDefault();
        return match is null ? sourcePath : Path.GetFullPath(match);
    }

    private static int PathMatchScore(string source, string candidate, string projectName)
    {
        if (!string.Equals(ProjectName(candidate), projectName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string[] sourceParts = ComparisonPath(source).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string[] candidateParts = ComparisonPath(candidate).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int trailing = 0;
        while (trailing < sourceParts.Length &&
               trailing < candidateParts.Length &&
               string.Equals(
                   sourceParts[^(trailing + 1)],
                   candidateParts[^(trailing + 1)],
                   StringComparison.OrdinalIgnoreCase))
        {
            trailing++;
        }

        return 100 + trailing * 10;
    }

    private static string ProjectName(string path)
    {
        string normalized = ComparisonPath(path).TrimEnd('\\');
        int separator = normalized.LastIndexOf('\\');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string ProjectDisplayName(string sourcePath, int projectlessSessions, int totalSessions)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return "无项目会话（无路径）";
        }

        string projectName = ProjectName(sourcePath);
        if (projectlessSessions == totalSessions)
        {
            return projectName + "（无项目）";
        }

        return projectlessSessions > 0 ? projectName + "（含无项目会话）" : projectName;
    }

    private static string ComparisonPath(string? path) =>
        CodexPathService.ToDesktopPath(path).Replace('/', '\\').TrimEnd('\\');

    private sealed record ManagedAttachmentFile(
        string FullPath,
        string RelativePath,
        string ComparisonFullPath);

    private sealed class PendingAttachment(ManagedAttachmentFile file)
    {
        public ManagedAttachmentFile File { get; } = file;
        public HashSet<string> SourceReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SessionIds { get; } = new(StringComparer.Ordinal);
    }
}
