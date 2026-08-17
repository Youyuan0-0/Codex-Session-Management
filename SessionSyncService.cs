using System.Diagnostics;

namespace CodexSessionHotSync;

internal sealed class SessionSyncService
{
    private readonly JsonlSessionService _jsonl = new();
    private readonly SqliteSessionService _sqlite = new();
    private readonly SessionIndexService _index = new();
    private readonly BackupService _backup = new();
    private readonly CodexGlobalStateService _globalState = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public async Task<InspectionSnapshot> InspectAsync(
        string? requestedCodexHome,
        bool includeArchived,
        string? requestedProvider = null,
        CancellationToken cancellationToken = default)
    {
        string codexHome = CodexConfigService.NormalizeCodexHome(requestedCodexHome);
        if (!Directory.Exists(codexHome))
        {
            throw new DirectoryNotFoundException("Codex Home 不存在：" + codexHome);
        }

        var (currentProvider, configuredProviders) = CodexConfigService.ReadProviders(codexHome);
        string targetProvider = string.IsNullOrWhiteSpace(requestedProvider)
            ? currentProvider
            : requestedProvider.Trim();
        IReadOnlyList<DatabaseLocation> locations = await _sqlite.DiscoverLocationsAsync(codexHome, cancellationToken);
        SessionScanResult scan = await _jsonl.ScanAsync(codexHome, includeArchived, cancellationToken);
        IReadOnlyList<SessionRecord> canonical = JsonlSessionService.SelectCanonicalSessions(scan.Sessions);
        var (indexEntries, indexNames) = await _index.ReadSummaryAsync(codexHome, cancellationToken);
        IReadOnlyList<DatabaseStatus> databases = await _sqlite.InspectAsync(
            locations,
            canonical,
            targetProvider,
            cancellationToken);
        IReadOnlyList<string> databaseProviders = await _sqlite.ReadProviderIdsAsync(locations, cancellationToken);
        PreparedGlobalStateFile globalState = await _globalState.PrepareAsync(
            codexHome,
            cancellationToken: cancellationToken);
        HashSet<string> providers = new(configuredProviders, StringComparer.OrdinalIgnoreCase)
        {
            currentProvider,
            targetProvider,
        };
        providers.UnionWith(scan.ProviderCounts.Keys.Where(item => item != "(missing)"));
        providers.UnionWith(databaseProviders);
        int missingIndex = canonical.Count(item => !indexNames.ContainsKey(item.Id));

        return new InspectionSnapshot
        {
            CodexHome = codexHome,
            CurrentProvider = currentProvider,
            ProviderOptions = providers.Where(item => !string.IsNullOrWhiteSpace(item)).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Scan = scan,
            CanonicalSessions = canonical,
            Databases = databases,
            IndexEntryCount = indexEntries,
            MissingIndexEntries = missingIndex,
            GlobalStateNeedsNormalization = globalState.Changed,
        };
    }

    public async Task<SyncResult> SyncAsync(
        string? requestedCodexHome,
        bool includeArchived,
        string targetProvider,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        SessionSyncOverrides? overrides = null,
        string? backupRoot = null)
    {
        if (string.IsNullOrWhiteSpace(targetProvider))
        {
            throw new InvalidOperationException("请选择或输入目标 Provider。 ");
        }

        await _operationLock.WaitAsync(cancellationToken);
        Stopwatch stopwatch = Stopwatch.StartNew();
        string? lockPath = null;
        string? backupDirectory = null;
        string? normalizedCodexHome = null;
        List<string> filesToRestore = [];
        bool indexExistedBefore = false;
        bool globalStateExistedBefore = false;
        FileStream? diskLock = null;
        try
        {
            string codexHome = CodexConfigService.NormalizeCodexHome(requestedCodexHome);
            normalizedCodexHome = codexHome;
            progress?.Report("扫描 JSONL 会话元数据...");
            InspectionSnapshot inspection = await InspectAsync(
                codexHome,
                includeArchived,
                targetProvider,
                cancellationToken);
            if (inspection.CanonicalSessions.Count == 0)
            {
                throw new InvalidOperationException("没有找到包含有效 session_meta 的会话 JSONL。 ");
            }

            lockPath = Path.Combine(codexHome, ".session-hot-sync.lock");
            try
            {
                diskLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error)
            {
                throw new InvalidOperationException("另一个会话同步任务正在运行。", error);
            }

            IReadOnlyList<DatabaseLocation> locations = await _sqlite.DiscoverLocationsAsync(codexHome, cancellationToken);
            IReadOnlyDictionary<string, string> databaseTitles = await _sqlite.ReadTitlesAsync(locations, cancellationToken);
            var (_, indexTitles) = await _index.ReadSummaryAsync(codexHome, cancellationToken);
            Dictionary<string, string> titles = new(databaseTitles, StringComparer.Ordinal);
            foreach ((string id, string title) in indexTitles)
            {
                titles[id] = title;
            }
            if (overrides is not null)
            {
                foreach ((string id, string title) in overrides.PreferredTitles)
                {
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
                    {
                        titles[id] = title;
                    }
                }
            }

            PreparedIndexFile preparedIndex = await _index.PrepareAsync(
                codexHome,
                inspection.CanonicalSessions,
                titles,
                cancellationToken);
            IReadOnlyList<SessionRecord> providerChangeCandidates = await _jsonl.FindProviderChangeCandidatesAsync(
                inspection.Scan.Sessions,
                targetProvider.Trim(),
                cancellationToken);
            PreparedGlobalStateFile globalState = await _globalState.PrepareAsync(
                codexHome,
                overrides?.WorkspaceRoots,
                overrides?.ProjectlessThreadIds,
                cancellationToken);
            globalStateExistedBefore = globalState.Exists;
            List<string> filesToBackUp = providerChangeCandidates
                .Select(item => item.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (globalState.Changed)
            {
                filesToBackUp.Add(globalState.Path);
            }
            indexExistedBefore = File.Exists(preparedIndex.Path);

            progress?.Report("创建一致性备份...");
            backupDirectory = await _backup.CreateAsync(
                codexHome,
                targetProvider.Trim(),
                locations,
                filesToBackUp,
                preparedIndex.Result.Changed ? preparedIndex.Path : null,
                backupRoot,
                cancellationToken);

            progress?.Report("更新 JSONL 的 session_meta...");
            JsonlProviderUpdateResult jsonlResult = await _jsonl.UpdateProvidersAsync(
                providerChangeCandidates,
                targetProvider.Trim(),
                cancellationToken);
            filesToRestore.AddRange(jsonlResult.ChangedPaths);

            progress?.Report("合并 session_index.jsonl...");
            await _index.WriteAsync(preparedIndex, cancellationToken);
            if (preparedIndex.Result.Changed)
            {
                filesToRestore.Add(preparedIndex.Path);
            }

            if (globalState.Changed)
            {
                progress?.Report("规范 Codex 工作区状态...");
                await _globalState.WriteAsync(globalState, cancellationToken);
                filesToRestore.Add(globalState.Path);
            }

            progress?.Report("检查用户消息标记...");
            HashSet<string> userEventThreadIds = new(StringComparer.Ordinal);
            foreach (SessionRecord session in inspection.CanonicalSessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await _jsonl.HasUserEventAsync(session.FilePath, cancellationToken))
                    {
                        userEventThreadIds.Add(session.Id);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }

            progress?.Report("事务同步两份 SQLite 数据库...");
            IReadOnlyList<DatabaseSyncStats> databaseResults = await _sqlite.SyncAsync(
                locations,
                inspection.CanonicalSessions,
                targetProvider.Trim(),
                titles,
                userEventThreadIds,
                globalState.ProjectlessThreadIds,
                cancellationToken);

            try
            {
                await _backup.PruneAsync(codexHome, backupRoot);
            }
            catch
            {
            }

            stopwatch.Stop();
            progress?.Report("同步完成");
            return new SyncResult
            {
                BackupDirectory = backupDirectory,
                TargetProvider = targetProvider.Trim(),
                ValidSessions = inspection.CanonicalSessions.Count,
                DuplicateJsonlFiles = inspection.DuplicateJsonlCount,
                Jsonl = jsonlResult,
                Databases = databaseResults,
                SessionIndex = preparedIndex.Result,
                GlobalStateUpdated = globalState.Changed,
                Duration = stopwatch.Elapsed,
            };
        }
        catch
        {
            if (backupDirectory is not null && normalizedCodexHome is not null && filesToRestore.Count > 0)
            {
                try
                {
                    await _backup.RestoreFilesAsync(
                        backupDirectory,
                        normalizedCodexHome,
                        filesToRestore,
                        CancellationToken.None);
                    string indexPath = Path.Combine(normalizedCodexHome, "session_index.jsonl");
                    if (!indexExistedBefore && filesToRestore.Contains(indexPath, StringComparer.OrdinalIgnoreCase))
                    {
                        File.Delete(indexPath);
                    }
                    string globalStatePath = Path.Combine(normalizedCodexHome, ".codex-global-state.json");
                    if (!globalStateExistedBefore &&
                        filesToRestore.Contains(globalStatePath, StringComparer.OrdinalIgnoreCase) &&
                        File.Exists(globalStatePath))
                    {
                        File.Delete(globalStatePath);
                    }
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            diskLock?.Dispose();
            if (lockPath is not null)
            {
                try
                {
                    File.Delete(lockPath);
                }
                catch
                {
                }
            }

            _operationLock.Release();
        }
    }
}
