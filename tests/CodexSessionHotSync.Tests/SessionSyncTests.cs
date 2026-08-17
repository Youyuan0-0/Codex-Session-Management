using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using CodexSessionHotSync;
using Microsoft.Data.Sqlite;

namespace CodexSessionHotSync.Tests;

public sealed class SessionSyncTests
{
    [Theory]
    [InlineData(1200, 96, (int)WorkspaceLayoutMode.Wide)]
    [InlineData(1000, 96, (int)WorkspaceLayoutMode.Compact)]
    [InlineData(760, 96, (int)WorkspaceLayoutMode.Narrow)]
    [InlineData(2200, 192, (int)WorkspaceLayoutMode.Wide)]
    [InlineData(1800, 192, (int)WorkspaceLayoutMode.Compact)]
    [InlineData(1520, 192, (int)WorkspaceLayoutMode.Narrow)]
    public void ResponsiveLayoutUsesLogicalWidth(
        int viewportWidth,
        int dpi,
        int expected)
    {
        Assert.Equal((WorkspaceLayoutMode)expected, MainForm.ResolveLayoutMode(viewportWidth, dpi));
    }

    [Fact]
    public async Task BackupPruningOnlyDeletesManagedSnapshotDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexSessionHotSyncTests", Guid.NewGuid().ToString("N"));
        string backupRoot = Path.Combine(root, "chosen-backups");
        try
        {
            Directory.CreateDirectory(backupRoot);
            string unmanaged = Path.Combine(backupRoot, "other-files");
            Directory.CreateDirectory(unmanaged);
            for (int index = 0; index < 11; index++)
            {
                string snapshot = Path.Combine(
                    backupRoot,
                    new DateTime(2026, 8, 1).AddDays(index).ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(snapshot);
                await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), "{}");
            }

            BackupService service = new();
            await service.PruneAsync(root, backupRoot, keepCount: 10);

            Assert.True(Directory.Exists(unmanaged));
            Assert.Equal(
                10,
                new DirectoryInfo(backupRoot).EnumerateDirectories()
                    .Count(item => File.Exists(Path.Combine(item.FullName, "manifest.json"))));
            Assert.Throws<InvalidOperationException>(() =>
                BackupService.ResolveRoot(root, Path.Combine(root, "sessions", "backups")));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task SyncsBothDatabasesFromJsonlAndRemainsIdempotent()
    {
        SqliteRuntime.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "CodexSessionHotSyncTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string customBackupRoot = Path.Combine(root, "chosen-backups");
            Directory.CreateDirectory(Path.Combine(root, "sqlite"));
            Directory.CreateDirectory(Path.Combine(root, "sessions", "2026", "07", "10"));
            await File.WriteAllTextAsync(Path.Combine(root, "config.toml"), "model_provider = \"custom\"\n");

            string id1 = "11111111-1111-1111-1111-111111111111";
            string id2 = "22222222-2222-2222-2222-222222222222";
            string orphan = "99999999-9999-9999-9999-999999999999";
            string file1 = Path.Combine(root, "sessions", "2026", "07", "10", $"rollout-2026-07-10T10-00-00-{id1}.jsonl");
            string file2 = Path.Combine(root, "sessions", "2026", "07", "10", $"rollout-2026-07-10T11-00-00-{id2}.jsonl");
            await WriteSessionAsync(file1, id1, "old-provider", "C:\\work\\one", "2026-07-10T02:00:00Z");
            await WriteSessionAsync(file2, id2, "other-provider", "C:\\work\\two", "2026-07-10T03:00:00Z");

            string indexPath = Path.Combine(root, "session_index.jsonl");
            await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(new
            {
                id = id1,
                thread_name = "First thread",
                updated_at = "2026-07-10T02:00:00Z",
            }) + Environment.NewLine);

            string legacyDb = Path.Combine(root, "state_5.sqlite");
            string modernDb = Path.Combine(root, "sqlite", "state_5.sqlite");
            await CreateDatabaseAsync(legacyDb);
            await CreateDatabaseAsync(modernDb);
            await InsertThreadAsync(legacyDb, id1, "C:\\missing\\one.jsonl", "old-provider", "First thread");
            await InsertThreadAsync(legacyDb, orphan, "C:\\missing\\orphan.jsonl", "old-provider", "Orphan");
            await InsertThreadAsync(modernDb, id2, "C:\\missing\\two.jsonl", "other-provider", "Second thread");

            SessionSyncService service = new();
            SyncResult first = await service.SyncAsync(root, true, "custom", backupRoot: customBackupRoot);

            Assert.Equal(2, first.ValidSessions);
            Assert.Equal(2, first.Jsonl.ChangedPaths.Count);
            Assert.Empty(first.Jsonl.SkippedPaths);
            Assert.True(Directory.Exists(first.BackupDirectory));
            Assert.Equal(
                Path.GetFullPath(customBackupRoot),
                Directory.GetParent(first.BackupDirectory)!.FullName);
            Assert.True(File.Exists(Path.Combine(first.BackupDirectory, "state_5.sqlite")));
            Assert.True(File.Exists(Path.Combine(first.BackupDirectory, "sqlite", "state_5.sqlite")));
            Assert.True(File.Exists(Path.Combine(first.BackupDirectory, "session_index.jsonl")));

            await AssertThreadAsync(legacyDb, id1, "custom", file1);
            await AssertThreadAsync(legacyDb, id2, "custom", file2);
            await AssertThreadAsync(modernDb, id1, "custom", file1);
            await AssertThreadAsync(modernDb, id2, "custom", file2);
            Assert.True(await ThreadExistsAsync(legacyDb, orphan));
            Assert.Equal("custom", await ReadThreadProviderAsync(legacyDb, orphan));
            Assert.False(await ThreadExistsAsync(modernDb, orphan));
            Assert.Equal("custom", await ReadJsonlProviderAsync(file1));
            Assert.Equal("custom", await ReadJsonlProviderAsync(file2));

            string[] indexLines = await File.ReadAllLinesAsync(indexPath);
            string[] indexIds = indexLines
                .Select(line => JsonNode.Parse(line)?["id"]?.GetValue<string>())
                .Where(id => id is not null)
                .Cast<string>()
                .ToArray();
            Assert.Equal(2, indexIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(id1, indexIds);
            Assert.Contains(id2, indexIds);

            SyncResult second = await service.SyncAsync(root, true, "custom", backupRoot: customBackupRoot);
            Assert.Empty(second.Jsonl.ChangedPaths);
            Assert.False(second.SessionIndex.Changed);
            Assert.All(second.Databases, item =>
            {
                Assert.Equal(0, item.InsertedRows);
                Assert.Equal(0, item.UpdatedRows);
                Assert.Equal(0, item.RepairedOrphans);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task RestoresProviderAcrossAllMetadataAndDiscoveredThreadDatabases()
    {
        SqliteRuntime.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "CodexSessionHotSyncTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sessions"));
            Directory.CreateDirectory(Path.Combine(root, "sqlite"));
            await File.WriteAllTextAsync(Path.Combine(root, "config.toml"), "model_provider = \"relay\"\n");

            string id = "33333333-3333-3333-3333-333333333333";
            string orphanRoot = "44444444-4444-4444-4444-444444444444";
            string orphanDynamic = "55555555-5555-5555-5555-555555555555";
            string rollout = Path.Combine(root, "sessions", $"rollout-2026-07-16T12-00-00-{id}.jsonl");
            await WriteMultiMetaSessionAsync(rollout, id);

            string rootDb = Path.Combine(root, "state_5.sqlite");
            string dynamicDb = Path.Combine(root, "sqlite", "codex-history.db");
            await CreateDatabaseAsync(rootDb);
            await CreateDatabaseAsync(dynamicDb);
            await InsertThreadAsync(rootDb, id, rollout, "openai", "Restored thread");
            await InsertThreadAsync(rootDb, orphanRoot, "C:\\missing\\root.jsonl", "openai", "Root orphan");
            await InsertThreadAsync(dynamicDb, id, rollout, "custom", "Restored thread");
            await InsertThreadAsync(dynamicDb, orphanDynamic, "C:\\missing\\dynamic.jsonl", "custom", "Dynamic orphan");

            await File.WriteAllTextAsync(
                Path.Combine(root, ".codex-global-state.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["projectless-thread-ids"] = new[] { id },
                    ["electron-saved-workspace-roots"] = new[] { @"\\?\C:\workspace", "C:/workspace" },
                    ["project-order"] = new[] { @"\\?\C:\workspace" },
                    ["active-workspace-roots"] = @"\\?\C:\workspace",
                    ["electron-workspace-root-labels"] = new Dictionary<string, string>
                    {
                        [@"\\?\C:\workspace"] = "Workspace",
                    },
                    ["open-in-target-preferences"] = new Dictionary<string, object>
                    {
                        ["perPath"] = new Dictionary<string, string>
                        {
                            [@"\\?\C:\workspace"] = "terminal",
                        },
                    },
                }));

            SessionSyncService service = new();
            SyncResult result = await service.SyncAsync(root, true, "relay");

            Assert.Equal(["relay", "relay"], await ReadAllJsonlProvidersAsync(rollout));
            Assert.Equal("relay", await ReadThreadProviderAsync(rootDb, id));
            Assert.Equal("relay", await ReadThreadProviderAsync(rootDb, orphanRoot));
            Assert.Equal("relay", await ReadThreadProviderAsync(dynamicDb, id));
            Assert.Equal("relay", await ReadThreadProviderAsync(dynamicDb, orphanDynamic));
            Assert.Contains(result.Databases, item => item.Label.Contains("codex-history.db", StringComparison.Ordinal));
            Assert.Equal(2, result.Databases.Sum(item => item.RepairedOrphans));
            Assert.True(result.GlobalStateUpdated);
            Assert.True(File.Exists(Path.Combine(result.BackupDirectory, "sqlite", "codex-history.db")));
            Assert.True(File.Exists(Path.Combine(result.BackupDirectory, ".codex-global-state.json")));

            JsonObject state = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(root, ".codex-global-state.json")))!.AsObject();
            Assert.Equal("C:/workspace", state["electron-saved-workspace-roots"]![0]!.GetValue<string>());
            Assert.Single(state["electron-saved-workspace-roots"]!.AsArray());
            Assert.Equal("C:/workspace", state["active-workspace-roots"]!.GetValue<string>());
            Assert.NotNull(state["electron-workspace-root-labels"]!["C:/workspace"]);
            Assert.NotNull(state["open-in-target-preferences"]!["perPath"]!["C:/workspace"]);

            SyncResult second = await service.SyncAsync(root, true, "relay");
            Assert.Empty(second.Jsonl.ChangedPaths);
            Assert.False(second.GlobalStateUpdated);
            Assert.All(second.Databases, item => Assert.Equal(0, item.RepairedOrphans));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ExportsAndImportsCompleteChatPackWithProjectMapping()
    {
        SqliteRuntime.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "CodexSessionHotSyncTests", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        string sourceProject = Path.Combine(root, "source-workspaces", "AppFlow");
        string targetProject = Path.Combine(root, "target-workspaces", "AppFlow");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "sessions", "2026", "08", "17"));
            Directory.CreateDirectory(Path.Combine(source, "sqlite"));
            Directory.CreateDirectory(Path.Combine(target, "sessions"));
            Directory.CreateDirectory(Path.Combine(target, "sqlite"));
            Directory.CreateDirectory(sourceProject);
            Directory.CreateDirectory(targetProject);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model_provider = \"openai\"\n");
            await File.WriteAllTextAsync(Path.Combine(target, "config.toml"), "model_provider = \"relay\"\n");

            string attachmentId = "77777777-7777-7777-7777-777777777777";
            string attachmentContent = string.Concat(Enumerable.Repeat(
                "The complete pasted text attachment must survive migration.\n",
                400));
            string sourceAttachment = Path.Combine(
                source,
                "attachments",
                attachmentId,
                "pasted-text.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceAttachment)!);
            await File.WriteAllTextAsync(sourceAttachment, attachmentContent);
            string sourceAttachmentReference = sourceAttachment.Replace('\\', '/');
            string unreferencedAttachment = Path.Combine(
                source,
                "attachments",
                "88888888-8888-8888-8888-888888888888",
                "pasted-text.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(unreferencedAttachment)!);
            await File.WriteAllTextAsync(unreferencedAttachment, "must not be exported");

            string targetAttachmentConflict = Path.Combine(
                target,
                "attachments",
                attachmentId,
                "pasted-text.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(targetAttachmentConflict)!);
            await File.WriteAllTextAsync(targetAttachmentConflict, "existing target attachment");

            string id = "66666666-6666-6666-6666-666666666666";
            string rollout = Path.Combine(
                source,
                "sessions",
                "2026",
                "08",
                "17",
                $"rollout-2026-08-17T10-00-00-{id}.jsonl");
            await WriteSessionAsync(
                rollout,
                id,
                "openai",
                sourceProject,
                "2026-08-17T02:00:00Z",
                sourceAttachmentReference);
            await File.WriteAllTextAsync(
                Path.Combine(source, "session_index.jsonl"),
                JsonSerializer.Serialize(new
                {
                    id,
                    thread_name = "AppFlow mapped conversation",
                    updated_at = "2026-08-17T02:00:00Z",
                }) + Environment.NewLine);

            string sourceLegacy = Path.Combine(source, "state_5.sqlite");
            string sourceModern = Path.Combine(source, "sqlite", "state_5.sqlite");
            await CreateDatabaseAsync(sourceLegacy);
            await CreateDatabaseAsync(sourceModern);
            await InsertThreadAsync(sourceLegacy, id, rollout, "openai", "AppFlow mapped conversation");
            await InsertThreadAsync(sourceModern, id, rollout, "openai", "AppFlow mapped conversation");

            string targetLegacy = Path.Combine(target, "state_5.sqlite");
            string targetModern = Path.Combine(target, "sqlite", "state_5.sqlite");
            await CreateDatabaseAsync(targetLegacy);
            await CreateDatabaseAsync(targetModern);

            string package = Path.Combine(root, "migration.codex-chatpack");
            ChatPackService chatPack = new();
            ChatPackExportResult exported = await chatPack.ExportAsync(source, true, package);
            Assert.Equal(1, exported.SessionCount);
            Assert.Equal(1, exported.ProjectCount);
            Assert.Equal(1, exported.AttachmentCount);
            Assert.True(File.Exists(package));
            using (ZipArchive archive = ZipFile.OpenRead(package))
            {
                Assert.Equal(3, archive.Entries.Count);
                Assert.NotNull(archive.GetEntry("manifest.json"));
                Assert.NotNull(archive.GetEntry($"conversations/{id}.jsonl"));
                ZipArchiveEntry attachmentEntry = Assert.IsType<ZipArchiveEntry>(
                    archive.GetEntry($"attachments/{attachmentId}/pasted-text.txt"));
                Assert.True(attachmentEntry.CompressedLength < attachmentEntry.Length);
                Assert.DoesNotContain(
                    archive.Entries,
                    entry => entry.FullName.Contains("88888888-8888-8888-8888-888888888888", StringComparison.Ordinal));
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("auth", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase));
            }

            ChatPackPreview preview = await chatPack.ReadPreviewAsync(package, target);
            ChatPackProjectMapping mapping = Assert.Single(preview.Mappings, item => item.RequiresPathMapping);
            Assert.Equal("AppFlow", mapping.ProjectName);
            mapping.TargetPath = targetProject;
            ChatPackImportResult imported = await chatPack.ImportAsync(preview, target, preview.Mappings);
            Assert.Equal(1, imported.ImportedSessions);
            Assert.Equal(1, imported.ImportedAttachments);
            Assert.Equal(0, imported.SkippedExistingSessions);
            string importedRollout = Assert.Single(
                imported.AddedPaths,
                path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
            string importedAttachment = Assert.Single(
                imported.AddedPaths,
                path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(targetAttachmentConflict, importedAttachment);
            Assert.Contains("-imported-", Path.GetFileName(importedAttachment), StringComparison.Ordinal);
            Assert.Equal(attachmentContent, await File.ReadAllTextAsync(importedAttachment));
            Assert.Equal("existing target attachment", await File.ReadAllTextAsync(targetAttachmentConflict));
            string importedMessage = await ReadFirstUserMessageAsync(importedRollout);
            Assert.Contains(CodexPathService.ToDesktopPath(importedAttachment), importedMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sourceAttachmentReference, importedMessage, StringComparison.OrdinalIgnoreCase);
            Assert.All(await ReadAllJsonlCwdsAsync(importedRollout), cwd => Assert.Equal(targetProject, cwd));

            SessionSyncService sync = new();
            SyncResult syncResult = await sync.SyncAsync(
                target,
                true,
                "relay",
                overrides: new SessionSyncOverrides
                {
                    PreferredTitles = imported.Titles,
                    WorkspaceRoots = imported.WorkspaceRoots,
                    ProjectlessThreadIds = imported.ProjectlessThreadIds,
                });
            Assert.Equal(1, syncResult.ValidSessions);
            await AssertThreadAsync(targetLegacy, id, "relay", importedRollout);
            await AssertThreadAsync(targetModern, id, "relay", importedRollout);
            Assert.Equal("AppFlow mapped conversation", await ReadThreadTitleAsync(targetLegacy, id));
            Assert.Equal(targetProject, await ReadThreadCwdAsync(targetLegacy, id));
            JsonObject globalState = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(target, ".codex-global-state.json")))!.AsObject();
            Assert.Contains(
                globalState["electron-saved-workspace-roots"]!.AsArray(),
                node => string.Equals(node!.GetValue<string>(), targetProject, StringComparison.OrdinalIgnoreCase));

            ChatPackImportResult duplicate = await chatPack.ImportAsync(preview, target, preview.Mappings);
            Assert.Equal(0, duplicate.ImportedSessions);
            Assert.Equal(0, duplicate.ImportedAttachments);
            Assert.Equal(1, duplicate.SkippedExistingSessions);
            Assert.Equal(
                2,
                Directory.GetFiles(Path.GetDirectoryName(targetAttachmentConflict)!, "*.txt", SearchOption.TopDirectoryOnly).Length);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task MapsProjectlessSessionWhenItStillHasAWorkingDirectory()
    {
        SqliteRuntime.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "CodexSessionHotSyncTests", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        string sourceProject = Path.Combine(root, "source-workspaces", "ni-h");
        string targetProject = Path.Combine(root, "target-workspaces", "ni-h");
        try
        {
            string sessions = Path.Combine(source, "sessions", "2026", "08", "17");
            Directory.CreateDirectory(sessions);
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(sourceProject);
            Directory.CreateDirectory(targetProject);
            await CreateDatabaseAsync(Path.Combine(target, "state_5.sqlite"));

            string id = "99999999-9999-9999-9999-999999999999";
            await WriteSessionAsync(
                Path.Combine(sessions, $"rollout-2026-08-17T12-30-00-{id}.jsonl"),
                id,
                "openai",
                sourceProject,
                "2026-08-17T04:30:00Z");
            await File.WriteAllTextAsync(
                Path.Combine(source, ".codex-global-state.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["projectless-thread-ids"] = new[] { id },
                }));

            string package = Path.Combine(root, "projectless-with-path.codex-chatpack");
            ChatPackService chatPack = new();
            ChatPackExportResult exported = await chatPack.ExportAsync(source, true, package);
            Assert.Equal(1, exported.SessionCount);
            Assert.Equal(1, exported.ProjectCount);

            ChatPackPreview preview = await chatPack.ReadPreviewAsync(package, target);
            ChatPackProjectMapping mapping = Assert.Single(preview.Mappings);
            Assert.True(mapping.RequiresPathMapping);
            Assert.Equal("ni-h（无项目）", mapping.ProjectName);
            Assert.Equal(sourceProject, mapping.SourcePath);
            mapping.TargetPath = targetProject;

            ChatPackImportResult imported = await chatPack.ImportAsync(preview, target, preview.Mappings);
            Assert.Equal(1, imported.ImportedSessions);
            Assert.Contains(id, imported.ProjectlessThreadIds);
            Assert.Contains(targetProject, imported.WorkspaceRoots, StringComparer.OrdinalIgnoreCase);

            SessionRecord importedSession = Assert.Single((await new JsonlSessionService()
                .ScanAsync(target, true)).Sessions);
            Assert.Equal(targetProject, importedSession.Cwd);

            await new SessionSyncService().SyncAsync(
                target,
                true,
                "relay",
                overrides: new SessionSyncOverrides
                {
                    PreferredTitles = imported.Titles,
                    WorkspaceRoots = imported.WorkspaceRoots,
                    ProjectlessThreadIds = imported.ProjectlessThreadIds,
                });
            JsonObject globalState = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(target, ".codex-global-state.json")))!.AsObject();
            Assert.Contains(
                globalState["projectless-thread-ids"]!.AsArray(),
                node => string.Equals(node!.GetValue<string>(), id, StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ExportCanIncludeOnlySelectedProjects()
    {
        SqliteRuntime.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "CodexSessionHotSyncTests", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string keepProject = Path.Combine(root, "workspaces", "KeepProject");
        string skipProject = Path.Combine(root, "workspaces", "SkipProject");
        try
        {
            string sessions = Path.Combine(source, "sessions", "2026", "08", "17");
            Directory.CreateDirectory(sessions);
            Directory.CreateDirectory(keepProject);
            Directory.CreateDirectory(skipProject);

            string keepId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            string skipId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
            string attachmentId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
            string skippedAttachment = Path.Combine(
                source,
                "attachments",
                attachmentId,
                "pasted-text.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(skippedAttachment)!);
            await File.WriteAllTextAsync(skippedAttachment, "only the skipped project references this attachment");

            await WriteSessionAsync(
                Path.Combine(sessions, $"rollout-2026-08-17T10-00-00-{keepId}.jsonl"),
                keepId,
                "openai",
                keepProject,
                "2026-08-17T02:00:00Z");
            await WriteSessionAsync(
                Path.Combine(sessions, $"rollout-2026-08-17T11-00-00-{skipId}.jsonl"),
                skipId,
                "openai",
                skipProject,
                "2026-08-17T03:00:00Z",
                CodexPathService.ToDesktopPath(skippedAttachment));

            ChatPackService chatPack = new();
            ChatPackExportPreview exportPreview = await chatPack.ReadExportPreviewAsync(source, true);
            Assert.Equal(2, exportPreview.Projects.Count);
            ChatPackExportProject selectedProject = Assert.Single(
                exportPreview.Projects,
                item => item.ProjectName == "KeepProject");
            Assert.Equal(keepId, Assert.Single(selectedProject.SessionIds));

            string package = Path.Combine(root, "selected-project.codex-chatpack");
            ChatPackExportResult exported = await chatPack.ExportAsync(
                source,
                true,
                package,
                selectedSessionIds: selectedProject.SessionIds);
            Assert.Equal(1, exported.SessionCount);
            Assert.Equal(1, exported.ProjectCount);
            Assert.Equal(0, exported.AttachmentCount);

            ChatPackPreview packagePreview = await chatPack.ReadPreviewAsync(package, source);
            ChatPackSessionEntry session = Assert.Single(packagePreview.Manifest.Sessions);
            Assert.Equal(keepId, session.Id);
            Assert.Empty(packagePreview.Manifest.Attachments);
            Assert.Equal("KeepProject", Assert.Single(packagePreview.Mappings).ProjectName);
            using ZipArchive archive = ZipFile.OpenRead(package);
            Assert.NotNull(archive.GetEntry($"conversations/{keepId}.jsonl"));
            Assert.Null(archive.GetEntry($"conversations/{skipId}.jsonl"));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("attachments/", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ImportCanExcludeAllSessionsFromAnUnselectedProject()
    {
        SqliteRuntime.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "CodexSessionHotSyncTests", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        string keepSourceProject = Path.Combine(root, "source-workspaces", "KeepProject");
        string skipSourceProject = Path.Combine(root, "source-workspaces", "SkipProject");
        string keepTargetProject = Path.Combine(root, "target-workspaces", "KeepProject");
        string skipTargetProject = Path.Combine(root, "target-workspaces", "SkipProject");
        try
        {
            string sessions = Path.Combine(source, "sessions", "2026", "08", "17");
            Directory.CreateDirectory(sessions);
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(keepSourceProject);
            Directory.CreateDirectory(skipSourceProject);
            Directory.CreateDirectory(keepTargetProject);
            Directory.CreateDirectory(skipTargetProject);

            string keepId = "77777777-7777-7777-7777-777777777777";
            string skipId = "88888888-8888-8888-8888-888888888888";
            await WriteSessionAsync(
                Path.Combine(sessions, $"rollout-2026-08-17T11-00-00-{keepId}.jsonl"),
                keepId,
                "openai",
                keepSourceProject,
                "2026-08-17T03:00:00Z");
            await WriteSessionAsync(
                Path.Combine(sessions, $"rollout-2026-08-17T12-00-00-{skipId}.jsonl"),
                skipId,
                "openai",
                skipSourceProject,
                "2026-08-17T04:00:00Z");

            string package = Path.Combine(root, "selective.codex-chatpack");
            ChatPackService chatPack = new();
            ChatPackExportResult exported = await chatPack.ExportAsync(source, true, package);
            Assert.Equal(2, exported.SessionCount);

            ChatPackPreview preview = await chatPack.ReadPreviewAsync(package, target);
            foreach (ChatPackProjectMapping mapping in preview.Mappings)
            {
                if (mapping.ProjectName == "KeepProject")
                {
                    mapping.TargetPath = keepTargetProject;
                    mapping.ImportSessions = true;
                }
                else if (mapping.ProjectName == "SkipProject")
                {
                    mapping.TargetPath = skipTargetProject;
                    mapping.ImportSessions = false;
                }
            }

            ChatPackImportResult imported = await chatPack.ImportAsync(preview, target, preview.Mappings);
            Assert.Equal(1, imported.ImportedSessions);
            Assert.Equal(1, imported.ExcludedSessions);
            Assert.Equal(0, imported.SkippedExistingSessions);
            Assert.Contains(keepId, imported.Titles.Keys);
            Assert.DoesNotContain(skipId, imported.Titles.Keys);

            SessionScanResult targetScan = await new JsonlSessionService().ScanAsync(target, true);
            SessionRecord importedSession = Assert.Single(targetScan.Sessions);
            Assert.Equal(keepId, importedSession.Id);
            Assert.Equal(keepTargetProject, importedSession.Cwd);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static async Task WriteSessionAsync(
        string path,
        string id,
        string provider,
        string cwd,
        string timestamp,
        string? attachmentReference = null)
    {
        string meta = JsonSerializer.Serialize(new
        {
            timestamp,
            type = "session_meta",
            payload = new
            {
                id,
                timestamp,
                model_provider = provider,
                cwd,
                source = "cli",
                cli_version = "1.0.0",
                history_mode = "legacy",
            },
        });
        string message = attachmentReference is null
            ? "test"
            : $"# Files mentioned by the user:\n\n## 粘贴的文本.txt: {attachmentReference}\n\n## My request for Codex:\n\ntest";
        string userEvent = JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new { type = "user_message", message },
        });
        await File.WriteAllTextAsync(path, meta + Environment.NewLine + userEvent + Environment.NewLine);
    }

    private static async Task<string> ReadFirstUserMessageAsync(string path)
    {
        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            if (JsonNode.Parse(line) is JsonObject record &&
                record["type"]?.GetValue<string>() == "event_msg" &&
                record["payload"]?["type"]?.GetValue<string>() == "user_message")
            {
                return record["payload"]?["message"]?.GetValue<string>() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static async Task WriteMultiMetaSessionAsync(string path, string id)
    {
        string prefix = JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new { type = "agent_reasoning", message = "before metadata" },
        });
        string firstMeta = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new
            {
                id,
                timestamp = "2026-07-16T04:00:00Z",
                model_provider = "openai",
                cwd = @"\\?\C:\workspace",
                source = "cli",
                cli_version = "1.0.0",
                history_mode = "legacy",
            },
        });
        string userEvent = JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new { type = "user_message", message = "test" },
        });
        string secondMeta = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new
            {
                id,
                timestamp = "2026-07-16T04:00:01Z",
                model_provider = "custom",
                cwd = @"\\?\C:\workspace",
            },
        });
        await File.WriteAllTextAsync(
            path,
            string.Join(Environment.NewLine, prefix, firstMeta, userEvent, secondMeta) + Environment.NewLine);
    }

    private static async Task CreateDatabaseAsync(string path)
    {
        await using SqliteConnection connection = Open(path);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE threads (
              id TEXT PRIMARY KEY,
              rollout_path TEXT NOT NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL,
              source TEXT NOT NULL,
              model_provider TEXT NOT NULL,
              cwd TEXT NOT NULL,
              title TEXT NOT NULL,
              sandbox_policy TEXT NOT NULL,
              approval_mode TEXT NOT NULL,
              tokens_used INTEGER NOT NULL,
              has_user_event INTEGER NOT NULL,
              archived INTEGER NOT NULL,
              archived_at INTEGER,
              cli_version TEXT NOT NULL,
              first_user_message TEXT NOT NULL,
              memory_mode TEXT NOT NULL,
              created_at_ms INTEGER,
              updated_at_ms INTEGER,
              preview TEXT NOT NULL,
              recency_at INTEGER NOT NULL,
              recency_at_ms INTEGER NOT NULL,
              history_mode TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertThreadAsync(
        string path,
        string id,
        string rolloutPath,
        string provider,
        string title)
    {
        await using SqliteConnection connection = Open(path);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO threads (
              id, rollout_path, created_at, updated_at, source, model_provider, cwd, title,
              sandbox_policy, approval_mode, tokens_used, has_user_event, archived, archived_at,
              cli_version, first_user_message, memory_mode, created_at_ms, updated_at_ms,
              preview, recency_at, recency_at_ms, history_mode
            ) VALUES (
              $id, $rollout, 100, 200, 'cli', $provider, 'C:\work', $title,
              '{"type":"disabled"}', 'never', 0, 1, 0, NULL,
              '1.0.0', $title, 'enabled', 100000, 200000,
              $title, 200, 200000, 'legacy'
            );
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$rollout", rolloutPath);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$title", title);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertThreadAsync(string path, string id, string provider, string rolloutPath)
    {
        await using SqliteConnection connection = Open(path);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT model_provider, rollout_path FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(provider, reader.GetString(0));
        Assert.Equal(Path.GetFullPath(rolloutPath), Path.GetFullPath(reader.GetString(1)));
    }

    private static async Task<bool> ThreadExistsAsync(string path, string id)
    {
        await using SqliteConnection connection = Open(path);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<string?> ReadThreadProviderAsync(string path, string id)
    {
        await using SqliteConnection connection = Open(path);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT model_provider FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadJsonlProviderAsync(string path)
    {
        string firstLine = (await File.ReadAllLinesAsync(path))[0];
        return JsonNode.Parse(firstLine)?["payload"]?["model_provider"]?.GetValue<string>();
    }

    private static async Task<string[]> ReadAllJsonlProvidersAsync(string path)
    {
        List<string> providers = [];
        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            if (JsonNode.Parse(line) is JsonObject record &&
                record["type"]?.GetValue<string>() == "session_meta" &&
                record["payload"]?["model_provider"]?.GetValue<string>() is { } provider)
            {
                providers.Add(provider);
            }
        }

        return providers.ToArray();
    }

    private static async Task<string[]> ReadAllJsonlCwdsAsync(string path)
    {
        List<string> paths = [];
        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            if (JsonNode.Parse(line) is JsonObject record &&
                record["type"]?.GetValue<string>() == "session_meta" &&
                record["payload"]?["cwd"]?.GetValue<string>() is { } cwd)
            {
                paths.Add(cwd);
            }
        }

        return paths.ToArray();
    }

    private static async Task<string?> ReadThreadTitleAsync(string path, string id)
    {
        await using SqliteConnection connection = Open(path);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadThreadCwdAsync(string path, string id)
    {
        await using SqliteConnection connection = Open(path);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT cwd FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString());
}
