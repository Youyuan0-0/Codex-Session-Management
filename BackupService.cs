using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexSessionHotSync;

internal sealed class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<string> CreateAsync(
        string codexHome,
        string targetProvider,
        IReadOnlyList<DatabaseLocation> databases,
        IReadOnlyCollection<string> jsonlFiles,
        string? sessionIndexPath,
        string? requestedBackupRoot = null,
        CancellationToken cancellationToken = default)
    {
        string root = ResolveRoot(codexHome, requestedBackupRoot);
        string backupDirectory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backupDirectory);
        List<string> backedUpFiles = [];

        foreach (DatabaseLocation database in databases.Where(item => File.Exists(item.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(backupDirectory, database.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await BackupDatabaseAsync(database.Path, destination, cancellationToken);
            backedUpFiles.Add(database.RelativePath);
        }

        foreach (string source in jsonlFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                continue;
            }

            string relative = Path.GetRelativePath(codexHome, source);
            string destination = Path.Combine(backupDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
            backedUpFiles.Add(relative);
        }

        if (!string.IsNullOrWhiteSpace(sessionIndexPath) && File.Exists(sessionIndexPath))
        {
            string relative = Path.GetRelativePath(codexHome, sessionIndexPath);
            string destination = Path.Combine(backupDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sessionIndexPath, destination, true);
            backedUpFiles.Add(relative);
        }

        BackupManifest manifest = new()
        {
            CreatedAt = DateTimeOffset.Now.ToString("O"),
            CodexHome = codexHome,
            TargetProvider = targetProvider,
            Files = backedUpFiles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);
        return backupDirectory;
    }

    public Task RestoreFilesAsync(
        string backupDirectory,
        string codexHome,
        IEnumerable<string> targetPaths,
        CancellationToken cancellationToken = default)
    {
        foreach (string targetPath in targetPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(codexHome, targetPath);
            string source = Path.Combine(backupDirectory, relative);
            if (!File.Exists(source))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(source, targetPath, true);
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(string codexHome, string? requestedBackupRoot = null, int keepCount = 10)
    {
        string root = ResolveRoot(codexHome, requestedBackupRoot);
        if (!Directory.Exists(root))
        {
            return Task.CompletedTask;
        }

        foreach (DirectoryInfo directory in new DirectoryInfo(root)
                     .EnumerateDirectories()
                     .Where(IsManagedBackupDirectory)
                     .OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .Skip(keepCount))
        {
            directory.Delete(true);
        }

        return Task.CompletedTask;
    }

    public static string ResolveRoot(string codexHome, string? requestedBackupRoot)
    {
        string normalizedCodexHome = Path.GetFullPath(codexHome);
        if (string.IsNullOrWhiteSpace(requestedBackupRoot))
        {
            return Path.Combine(normalizedCodexHome, "backups", "session-hot-sync");
        }

        string expanded = Environment.ExpandEnvironmentVariables(requestedBackupRoot.Trim());
        string root = Path.GetFullPath(expanded);
        string[] protectedDirectories =
        [
            Path.Combine(normalizedCodexHome, "sessions"),
            Path.Combine(normalizedCodexHome, "archived_sessions"),
            Path.Combine(normalizedCodexHome, "sqlite"),
            Path.Combine(normalizedCodexHome, "attachments"),
        ];
        if (protectedDirectories.Any(path => IsSameOrDescendant(root, path)))
        {
            throw new InvalidOperationException(
                "备份保存路径不能位于 Codex 的 sessions、archived_sessions、sqlite 或 attachments 数据目录中。");
        }

        return root;
    }

    private static bool IsManagedBackupDirectory(DirectoryInfo directory) =>
        DateTime.TryParseExact(
            directory.Name,
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _) &&
        File.Exists(Path.Combine(directory.FullName, "manifest.json"));

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(
                   normalizedParent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task BackupDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection source = OpenConnection(sourcePath, SqliteOpenMode.ReadOnly);
        await using SqliteConnection destination = OpenConnection(destinationPath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        await using (SqliteCommand timeout = source.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 5000";
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        source.BackupDatabase(destination);
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
    }
}
