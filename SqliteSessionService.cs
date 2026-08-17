using Microsoft.Data.Sqlite;

namespace CodexSessionHotSync;

internal sealed class SqliteSessionService
{
    private sealed record ColumnInfo(string Name, string Type, bool NotNull, string? DefaultValue, bool PrimaryKey);
    private sealed class MutableStats
    {
        public required string Label { get; init; }
        public int InsertedRows { get; set; }
        public int UpdatedRows { get; set; }
        public int SkippedOrphans { get; set; }
        public int RepairedOrphans { get; set; }
    }

    public async Task<IReadOnlyList<DatabaseLocation>> DiscoverLocationsAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DatabaseLocation> fixedLocations = CodexConfigService.DatabaseLocations(codexHome);
        DatabaseLocation legacy = fixedLocations.First(item => item.Key == "legacy");
        DatabaseLocation modernPlaceholder = fixedLocations.First(item => item.Key == "modern");
        string sqliteDirectory = Path.Combine(codexHome, "sqlite");
        List<string> sqliteCandidates = [modernPlaceholder.Path];
        if (Directory.Exists(sqliteDirectory))
        {
            sqliteCandidates.AddRange(Directory.EnumerateFiles(sqliteDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSqliteCandidate));
        }

        List<string> threadDatabases = [];
        foreach (string path in sqliteCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path) && await IsThreadDatabaseAsync(path, cancellationToken))
            {
                threadDatabases.Add(Path.GetFullPath(path));
            }
        }

        threadDatabases = threadDatabases
            .OrderBy(path => string.Equals(path, modernPlaceholder.Path, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<DatabaseLocation> result = [legacy];
        if (threadDatabases.Count == 0)
        {
            result.Add(modernPlaceholder);
            return result;
        }

        string primaryModernPath = threadDatabases[0];
        string primaryModernRelative = Path.GetRelativePath(codexHome, primaryModernPath);
        result.Add(new DatabaseLocation(
            "modern",
            string.Equals(primaryModernPath, modernPlaceholder.Path, StringComparison.OrdinalIgnoreCase)
                ? "sqlite 目录数据库"
                : primaryModernRelative.Replace('\\', '/'),
            primaryModernPath,
            primaryModernRelative));
        for (int index = 1; index < threadDatabases.Count; index++)
        {
            string path = threadDatabases[index];
            result.Add(new DatabaseLocation(
                "sqlite-" + index,
                "sqlite/" + Path.GetFileName(path),
                path,
                Path.GetRelativePath(codexHome, path)));
        }

        return result;
    }

    public async Task<IReadOnlyList<DatabaseStatus>> InspectAsync(
        IReadOnlyList<DatabaseLocation> locations,
        IReadOnlyList<SessionRecord> sessions,
        string targetProvider,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, SessionRecord> valid = sessions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        List<DatabaseStatus> result = [];
        foreach (DatabaseLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(location.Path))
            {
                result.Add(new DatabaseStatus
                {
                    Location = location,
                    Exists = false,
                    Readable = false,
                    MissingFromDatabase = valid.Count,
                });
                continue;
            }

            try
            {
                await using SqliteConnection connection = OpenConnection(location.Path, SqliteOpenMode.ReadOnly);
                await connection.OpenAsync(cancellationToken);
                await SetBusyTimeoutAsync(connection, cancellationToken);
                if (!await TableExistsAsync(connection, "main", "threads", cancellationToken))
                {
                    throw new InvalidOperationException("缺少 threads 表");
                }

                IReadOnlyList<ColumnInfo> columns = await ReadColumnsAsync(connection, "main", cancellationToken);
                HashSet<string> names = columns.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string rolloutExpression = names.Contains("rollout_path") ? Quote("rollout_path") : "''";
                string providerExpression = names.Contains("model_provider") ? Quote("model_provider") : "''";
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $"SELECT {Quote("id")}, {rolloutExpression}, {providerExpression} FROM {Qualified("main", "threads")}";
                int total = 0;
                int validRows = 0;
                int orphanRows = 0;
                int wrongProviderRows = 0;
                int wrongPathRows = 0;
                HashSet<string> present = new(StringComparer.Ordinal);
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    total++;
                    string id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    string provider = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    if (!string.Equals(provider, targetProvider, StringComparison.Ordinal))
                    {
                        wrongProviderRows++;
                    }

                    if (!valid.TryGetValue(id, out SessionRecord? session))
                    {
                        orphanRows++;
                        continue;
                    }

                    present.Add(id);
                    validRows++;
                    string rolloutPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (!PathsEqual(rolloutPath, session.FilePath))
                    {
                        wrongPathRows++;
                    }
                }

                result.Add(new DatabaseStatus
                {
                    Location = location,
                    Exists = true,
                    Readable = true,
                    TotalRows = total,
                    ValidRows = validRows,
                    MissingJsonlRows = orphanRows,
                    MissingFromDatabase = valid.Count - present.Count,
                    WrongProviderRows = wrongProviderRows,
                    WrongRolloutPathRows = wrongPathRows,
                });
            }
            catch (Exception error)
            {
                result.Add(new DatabaseStatus
                {
                    Location = location,
                    Exists = true,
                    Readable = false,
                    Error = FriendlySqliteMessage(error),
                    MissingFromDatabase = valid.Count,
                });
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> ReadProviderIdsAsync(
        IReadOnlyList<DatabaseLocation> locations,
        CancellationToken cancellationToken = default)
    {
        HashSet<string> providers = new(StringComparer.OrdinalIgnoreCase);
        foreach (DatabaseLocation location in locations.Where(item => File.Exists(item.Path)))
        {
            try
            {
                await using SqliteConnection connection = OpenConnection(location.Path, SqliteOpenMode.ReadOnly);
                await connection.OpenAsync(cancellationToken);
                if (!await TableExistsAsync(connection, "main", "threads", cancellationToken))
                {
                    continue;
                }

                IReadOnlyList<ColumnInfo> columns = await ReadColumnsAsync(connection, "main", cancellationToken);
                if (!columns.Any(item => string.Equals(item.Name, "model_provider", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $"SELECT DISTINCT {Quote("model_provider")} FROM {Qualified("main", "threads")} WHERE COALESCE({Quote("model_provider")}, '') <> ''";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    providers.Add(reader.GetString(0));
                }
            }
            catch
            {
            }
        }

        return providers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadTitlesAsync(
        IReadOnlyList<DatabaseLocation> locations,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, (string Title, long Timestamp)> titles = new(StringComparer.Ordinal);
        foreach (DatabaseLocation location in locations.Where(item => File.Exists(item.Path)))
        {
            try
            {
                await using SqliteConnection connection = OpenConnection(location.Path, SqliteOpenMode.ReadOnly);
                await connection.OpenAsync(cancellationToken);
                IReadOnlyDictionary<string, ThreadRow> rows = await ReadRowsAsync(connection, "main", cancellationToken);
                foreach ((string id, ThreadRow row) in rows)
                {
                    string title = Convert.ToString(row.Values.GetValueOrDefault("title")) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    if (!titles.TryGetValue(id, out var existing) || row.SortTimestamp >= existing.Timestamp)
                    {
                        titles[id] = (title, row.SortTimestamp);
                    }
                }
            }
            catch
            {
            }
        }

        return titles.ToDictionary(item => item.Key, item => item.Value.Title, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<DatabaseSyncStats>> SyncAsync(
        IReadOnlyList<DatabaseLocation> locations,
        IReadOnlyList<SessionRecord> sessions,
        string targetProvider,
        IReadOnlyDictionary<string, string> titles,
        IReadOnlySet<string> userEventThreadIds,
        IReadOnlySet<string> projectlessThreadIds,
        CancellationToken cancellationToken = default)
    {
        List<DatabaseLocation> existing = [];
        foreach (DatabaseLocation location in locations.Where(item => File.Exists(item.Path)))
        {
            if (await IsThreadDatabaseAsync(location.Path, cancellationToken))
            {
                existing.Add(location);
            }
        }
        if (existing.Count == 0)
        {
            throw new InvalidOperationException("未找到 state_5.sqlite，无法同步 SQLite 线程索引。 ");
        }

        await using SqliteConnection connection = OpenConnection(existing[0].Path, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(cancellationToken);
        await SetBusyTimeoutAsync(connection, cancellationToken);

        List<(string Schema, DatabaseLocation Location)> schemas = [("main", existing[0])];
        for (int index = 1; index < existing.Count; index++)
        {
            await using SqliteCommand attach = connection.CreateCommand();
            string schema = "peer" + index;
            attach.CommandText = $"ATTACH DATABASE $path AS {Quote(schema)}";
            attach.Parameters.AddWithValue("$path", existing[index].Path);
            await attach.ExecuteNonQueryAsync(cancellationToken);
            schemas.Add((schema, existing[index]));
        }

        bool transactionOpen = false;
        try
        {
            await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE", cancellationToken);
            transactionOpen = true;
            Dictionary<string, IReadOnlyList<ColumnInfo>> columnsBySchema = new(StringComparer.Ordinal);
            Dictionary<string, IReadOnlyDictionary<string, ThreadRow>> rowsBySchema = new(StringComparer.Ordinal);
            HashSet<string> validIds = sessions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            Dictionary<string, MutableStats> stats = new(StringComparer.Ordinal);

            foreach ((string schema, DatabaseLocation location) in schemas)
            {
                if (!await TableExistsAsync(connection, schema, "threads", cancellationToken))
                {
                    throw new InvalidOperationException($"{location.Label} 缺少 threads 表。 ");
                }

                IReadOnlyList<ColumnInfo> columns = await ReadColumnsAsync(connection, schema, cancellationToken);
                IReadOnlyDictionary<string, ThreadRow> rows = await ReadRowsAsync(connection, schema, cancellationToken);
                columnsBySchema[schema] = columns;
                rowsBySchema[schema] = rows;
                stats[schema] = new MutableStats
                {
                    Label = location.Label,
                    SkippedOrphans = rows.Keys.Count(id => !validIds.Contains(id)),
                };
            }

            foreach (SessionRecord session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<ThreadRow> candidates = schemas
                    .Select(item => rowsBySchema[item.Schema].GetValueOrDefault(session.Id))
                    .Where(item => item is not null)
                    .Cast<ThreadRow>()
                    .OrderByDescending(item => item.SortTimestamp)
                    .ToList();
                Dictionary<string, object?> merged = MergeRows(candidates);
                ApplySessionValues(
                    merged,
                    session,
                    targetProvider,
                    titles.GetValueOrDefault(session.Id),
                    userEventThreadIds.Contains(session.Id),
                    projectlessThreadIds.Contains(session.Id));

                foreach ((string schema, _) in schemas)
                {
                    IReadOnlyList<ColumnInfo> columns = columnsBySchema[schema];
                    ThreadRow? existingRow = rowsBySchema[schema].GetValueOrDefault(session.Id);
                    object?[] desired = BuildDesiredValues(columns, merged);
                    if (existingRow is not null && RowMatches(columns, existingRow, desired))
                    {
                        continue;
                    }

                    await UpsertThreadAsync(connection, schema, columns, desired, cancellationToken);
                    if (existingRow is null)
                    {
                        stats[schema].InsertedRows++;
                    }
                    else
                    {
                        stats[schema].UpdatedRows++;
                    }
                }
            }

            foreach ((string schema, _) in schemas)
            {
                await using SqliteCommand repairOrphans = connection.CreateCommand();
                repairOrphans.CommandText = $"UPDATE {Qualified(schema, "threads")} SET {Quote("model_provider")} = $provider WHERE COALESCE({Quote("model_provider")}, '') <> $provider";
                repairOrphans.Parameters.AddWithValue("$provider", targetProvider);
                stats[schema].RepairedOrphans += await repairOrphans.ExecuteNonQueryAsync(cancellationToken);
            }

            await ExecuteNonQueryAsync(connection, "COMMIT", cancellationToken);
            transactionOpen = false;
            return schemas.Select(item =>
            {
                MutableStats value = stats[item.Schema];
                return new DatabaseSyncStats
                {
                    Label = value.Label,
                    InsertedRows = value.InsertedRows,
                    UpdatedRows = value.UpdatedRows,
                    SkippedOrphans = value.SkippedOrphans,
                    RepairedOrphans = value.RepairedOrphans,
                };
            }).ToArray();
        }
        catch (Exception error)
        {
            if (transactionOpen)
            {
                try
                {
                    await ExecuteNonQueryAsync(connection, "ROLLBACK", CancellationToken.None);
                }
                catch
                {
                }
            }

            throw new InvalidOperationException(FriendlySqliteMessage(error), error);
        }
    }

    private static Dictionary<string, object?> MergeRows(IReadOnlyList<ThreadRow> rows)
    {
        Dictionary<string, object?> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (ThreadRow row in rows)
        {
            foreach ((string key, object? value) in row.Values)
            {
                if (!merged.TryGetValue(key, out object? current) || IsEmpty(current))
                {
                    merged[key] = value;
                }
            }
        }

        return merged;
    }

    private static void ApplySessionValues(
        Dictionary<string, object?> values,
        SessionRecord session,
        string targetProvider,
        string? title,
        bool hasUserEvent,
        bool projectless)
    {
        long createdSeconds = session.CreatedAt.ToUnixTimeSeconds();
        long updatedSeconds = session.UpdatedAt.ToUnixTimeSeconds();
        long createdMilliseconds = session.CreatedAt.ToUnixTimeMilliseconds();
        long updatedMilliseconds = session.UpdatedAt.ToUnixTimeMilliseconds();
        string effectiveTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : Convert.ToString(values.GetValueOrDefault("title")) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(effectiveTitle))
        {
            effectiveTitle = "会话 " + session.Id[..Math.Min(8, session.Id.Length)];
        }

        values["id"] = session.Id;
        values["rollout_path"] = session.FilePath;
        values["model_provider"] = targetProvider;
        values["archived"] = session.Archived ? 1L : 0L;
        values["title"] = effectiveTitle;
        values["preview"] = NonEmpty(values.GetValueOrDefault("preview"), effectiveTitle);
        values["first_user_message"] = NonEmpty(values.GetValueOrDefault("first_user_message"), effectiveTitle);
        values["source"] = NonEmpty(values.GetValueOrDefault("source"), session.Source);
        if (!projectless)
        {
            values["cwd"] = NonEmpty(session.Cwd, Convert.ToString(values.GetValueOrDefault("cwd")) ?? string.Empty);
        }
        values["cli_version"] = NonEmpty(values.GetValueOrDefault("cli_version"), session.CliVersion);
        values["history_mode"] = NonEmpty(values.GetValueOrDefault("history_mode"), session.HistoryMode);
        values["memory_mode"] = NonEmpty(values.GetValueOrDefault("memory_mode"), "enabled");
        values["sandbox_policy"] = NonEmpty(values.GetValueOrDefault("sandbox_policy"), "{\"type\":\"disabled\"}");
        values["approval_mode"] = NonEmpty(values.GetValueOrDefault("approval_mode"), "never");
        values["tokens_used"] = Numeric(values.GetValueOrDefault("tokens_used"), 0L);
        values["has_user_event"] = hasUserEvent || Numeric(values.GetValueOrDefault("has_user_event"), 0L) != 0 ? 1L : 0L;
        if (!string.IsNullOrWhiteSpace(session.ThreadSource))
        {
            values["thread_source"] = NonEmpty(values.GetValueOrDefault("thread_source"), session.ThreadSource);
        }

        values["created_at"] = PositiveMin(values.GetValueOrDefault("created_at"), createdSeconds);
        values["created_at_ms"] = PositiveMin(values.GetValueOrDefault("created_at_ms"), createdMilliseconds);
        values["updated_at"] = PositiveMax(values.GetValueOrDefault("updated_at"), updatedSeconds);
        values["updated_at_ms"] = PositiveMax(values.GetValueOrDefault("updated_at_ms"), updatedMilliseconds);
        values["recency_at"] = PositiveMax(values.GetValueOrDefault("recency_at"), updatedSeconds);
        values["recency_at_ms"] = PositiveMax(values.GetValueOrDefault("recency_at_ms"), updatedMilliseconds);
    }

    private static object?[] BuildDesiredValues(IReadOnlyList<ColumnInfo> columns, IReadOnlyDictionary<string, object?> values)
    {
        object?[] result = new object?[columns.Count];
        for (int index = 0; index < columns.Count; index++)
        {
            ColumnInfo column = columns[index];
            values.TryGetValue(column.Name, out object? value);
            if (value is null or DBNull)
            {
                value = column.NotNull ? DefaultValue(column) : null;
            }

            result[index] = value;
        }

        return result;
    }

    private static async Task UpsertThreadAsync(
        SqliteConnection connection,
        string schema,
        IReadOnlyList<ColumnInfo> columns,
        object?[] values,
        CancellationToken cancellationToken)
    {
        if (!columns.Any(item => string.Equals(item.Name, "id", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("threads 表缺少 id 列。 ");
        }

        string columnList = string.Join(", ", columns.Select(item => Quote(item.Name)));
        string parameterList = string.Join(", ", columns.Select((_, index) => "$p" + index));
        string updateList = string.Join(", ", columns
            .Where(item => !string.Equals(item.Name, "id", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{Quote(item.Name)} = excluded.{Quote(item.Name)}"));
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {Qualified(schema, "threads")} ({columnList}) VALUES ({parameterList}) ON CONFLICT({Quote("id")}) DO UPDATE SET {updateList}";
        for (int index = 0; index < values.Length; index++)
        {
            command.Parameters.AddWithValue("$p" + index, values[index] ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool RowMatches(IReadOnlyList<ColumnInfo> columns, ThreadRow existing, object?[] desired)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            existing.Values.TryGetValue(columns[index].Name, out object? current);
            if (!DatabaseValuesEqual(current, desired[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DatabaseValuesEqual(object? left, object? right)
    {
        if (left is null or DBNull)
        {
            return right is null or DBNull;
        }

        if (right is null or DBNull)
        {
            return false;
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        if (IsNumeric(left) && IsNumeric(right))
        {
            return Convert.ToDecimal(left) == Convert.ToDecimal(right);
        }

        return string.Equals(Convert.ToString(left), Convert.ToString(right), StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyDictionary<string, ThreadRow>> ReadRowsAsync(
        SqliteConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ThreadRow> rows = new(StringComparer.Ordinal);
        if (!await TableExistsAsync(connection, schema, "threads", cancellationToken))
        {
            return rows;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Qualified(schema, "threads")}";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            string id = Convert.ToString(values.GetValueOrDefault("id")) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(id))
            {
                rows[id] = new ThreadRow(values);
            }
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ColumnInfo>> ReadColumnsAsync(
        SqliteConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        List<ColumnInfo> columns = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {schema}.table_info(threads)";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnInfo(
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5) != 0));
        }

        return columns;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {Quote(schema)}.sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool IsSqliteCandidate(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsThreadDatabaseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = OpenConnection(path, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken);
            if (!await TableExistsAsync(connection, "main", "threads", cancellationToken))
            {
                return false;
            }

            IReadOnlyList<ColumnInfo> columns = await ReadColumnsAsync(connection, "main", cancellationToken);
            return columns.Any(item => string.Equals(item.Name, "model_provider", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static async Task SetBusyTimeoutAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 5000", cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string text,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static string Qualified(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsEmpty(object? value) => value is null or DBNull || string.IsNullOrWhiteSpace(Convert.ToString(value));
    private static object NonEmpty(object? value, object fallback) => IsEmpty(value) ? fallback : value!;
    private static long Numeric(object? value, long fallback)
    {
        try
        {
            return value is null or DBNull ? fallback : Convert.ToInt64(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static long PositiveMin(object? value, long fallback)
    {
        long current = Numeric(value, 0);
        return current > 0 ? Math.Min(current, fallback) : fallback;
    }

    private static long PositiveMax(object? value, long fallback)
    {
        long current = Numeric(value, 0);
        return current > 0 ? Math.Max(current, fallback) : fallback;
    }

    private static bool IsNumeric(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private static object? DefaultValue(ColumnInfo column)
    {
        string type = column.Type.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return 0L;
        }

        if (type.Contains("REAL", StringComparison.Ordinal) ||
            type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal))
        {
            return 0d;
        }

        if (type.Contains("BLOB", StringComparison.Ordinal))
        {
            return Array.Empty<byte>();
        }

        return string.Empty;
    }

    private static string FriendlySqliteMessage(Exception error)
    {
        Exception current = error;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6)
        {
            return "SQLite 正被 Codex 占用，当前没有写入任何数据库更改。请稍后重试。";
        }

        return "SQLite 同步失败：" + error.Message;
    }
}
