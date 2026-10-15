using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Companion.Core;

public sealed class DatabaseService : IDatabaseService
{
    private const int BusyTimeoutMilliseconds = 5000;
    public const int DefaultClipboardLimit = 10;
    private readonly string _connectionString;

    public event Action? ScheduledActionsChanged;
    public event Action? TodosChanged;
    public event Action? MemoriesChanged;

    public DatabaseService(string? databasePath = null)
    {
        string path = databasePath ?? AppPaths.DatabasePath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000
        }.ToString();

        Initialize();
    }

    // Memories CRUD

    public long AddMemory(string fact, string? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fact);
        long id;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO memories (fact, tags, created_utc) VALUES ($fact, $tags, $createdUtc);";
            command.Parameters.AddWithValue("$fact", fact.Trim());
            command.Parameters.AddWithValue("$tags", (object?)tags?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            id = GetLastInsertRowId(connection);
        }

        MemoriesChanged?.Invoke();
        return id;
    }

    public long InsertMemory(string fact, string? tags = null) => AddMemory(fact, tags);

    public MemoryEntry? GetMemory(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, fact, tags, created_utc FROM memories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadMemory(reader) : null;
    }

    public IReadOnlyList<MemoryEntry> GetAllMemories()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, fact, tags, created_utc FROM memories ORDER BY created_utc DESC, id DESC;";
        var results = new List<MemoryEntry>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadMemory(reader));
        }

        return results;
    }

    public bool UpdateMemory(long id, string fact, string? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fact);
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE memories SET fact = $fact, tags = $tags WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$fact", fact.Trim());
            command.Parameters.AddWithValue("$tags", (object?)tags?.Trim() ?? DBNull.Value);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            MemoriesChanged?.Invoke();
        }

        return changed > 0;
    }

    public bool DeleteMemory(long id)
    {
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM memories WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            MemoriesChanged?.Invoke();
        }

        return changed > 0;
    }

    public IReadOnlyList<MemoryEntry> SearchMemoriesFts(string query, int limit = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
        }

        string sanitizedQuery = SanitizeFts5Query(query);

        // If query has no alphanumeric tokens, execute safe LIKE fallback immediately
        if (string.IsNullOrWhiteSpace(sanitizedQuery))
        {
            return SearchMemoriesLikeFallback(query, limit);
        }

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.id, m.fact, m.tags, m.created_utc
                FROM memories_fts AS f
                JOIN memories AS m ON m.id = f.rowid
                WHERE memories_fts MATCH $query
                ORDER BY bm25(memories_fts, 10.0, 5.0), m.created_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", sanitizedQuery);
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<MemoryEntry>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadMemory(reader));
            }

            return results;
        }
        catch (SqliteException)
        {
            // Resilient fallback on any FTS query anomaly
            return SearchMemoriesLikeFallback(query, limit);
        }
    }

    public static string SanitizeFts5Query(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var terms = new List<string>();
        var matches = Regex.Matches(query, @"[\p{L}\p{N}_]+");

        foreach (Match match in matches)
        {
            string word = match.Value;
            if (word.Length == 0) continue;

            if (word.Length < 3)
            {
                terms.Add($"\"{word}\"");
            }
            else
            {
                terms.Add($"\"{word}\"*");
            }
        }

        return terms.Count == 0 ? string.Empty : string.Join(" ", terms);
    }

    private IReadOnlyList<MemoryEntry> SearchMemoriesLikeFallback(string query, int limit)
    {
        string escaped = query.Trim().Replace("%", "\\%").Replace("_", "\\_");
        string pattern = $"%{escaped}%";

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, fact, tags, created_utc
            FROM memories
            WHERE fact LIKE $pattern ESCAPE '\' OR tags LIKE $pattern ESCAPE '\'
            ORDER BY created_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", pattern);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<MemoryEntry>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadMemory(reader));
        }

        return results;
    }

    // Todos CRUD

    public long AddTodo(string title, string? dueTimeText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        long id;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO todos (title, due_time, created_utc) VALUES ($title, $dueTime, $createdUtc);";
            command.Parameters.AddWithValue("$title", title.Trim());
            command.Parameters.AddWithValue("$dueTime", (object?)dueTimeText?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            id = GetLastInsertRowId(connection);
        }

        TodosChanged?.Invoke();
        return id;
    }

    public IReadOnlyList<TodoItem> GetTodos()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, due_time, is_completed, created_utc FROM todos ORDER BY is_completed, due_time IS NULL, due_time, id;";
        var results = new List<TodoItem>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TodoItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3) != 0,
                ParseUtc(reader.GetString(4))));
        }

        return results;
    }

    public IReadOnlyList<TodoItem> GetActiveTodos()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, due_time, is_completed, created_utc FROM todos WHERE is_completed = 0 ORDER BY due_time IS NULL, due_time, id;";
        var results = new List<TodoItem>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TodoItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3) != 0,
                ParseUtc(reader.GetString(4))));
        }

        return results;
    }

    public TodoItem? GetTodo(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, due_time, is_completed, created_utc FROM todos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new TodoItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3) != 0,
                ParseUtc(reader.GetString(4)));
        }

        return null;
    }

    public bool ToggleTodo(long id)
    {
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE todos SET is_completed = CASE is_completed WHEN 0 THEN 1 ELSE 0 END WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            TodosChanged?.Invoke();
        }

        return changed > 0;
    }

    public bool UpdateTodo(long id, string title, string? dueTimeText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE todos SET title = $title, due_time = $dueTime WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title.Trim());
            command.Parameters.AddWithValue("$dueTime", (object?)dueTimeText?.Trim() ?? DBNull.Value);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            TodosChanged?.Invoke();
        }

        return changed > 0;
    }

    public bool UpdateTodo(long id, string title, string? dueTimeText, bool isCompleted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE todos SET title = $title, due_time = $dueTime, is_completed = $isCompleted WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title.Trim());
            command.Parameters.AddWithValue("$dueTime", (object?)dueTimeText?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("$isCompleted", isCompleted ? 1 : 0);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            TodosChanged?.Invoke();
        }

        return changed > 0;
    }

    public bool DeleteTodo(long id)
    {
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM todos WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            TodosChanged?.Invoke();
        }

        return changed > 0;
    }

    public IReadOnlyList<Routine> GetRoutines()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, steps_json, created_utc, is_enabled
            FROM routines
            ORDER BY name COLLATE NOCASE, id;
            """;
        var routines = new List<Routine>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            routines.Add(ReadRoutine(reader));
        }

        return routines;
    }

    public Routine? GetRoutine(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, steps_json, created_utc, is_enabled
            FROM routines WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadRoutine(reader) : null;
    }

    public long AddRoutine(string name, string stepsJson, DateTime nextTriggerUtc, string timeHhmm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stepsJson);
        if (!TimeOnly.TryParseExact(timeHhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException("Routine time must use HH:mm format.", nameof(timeHhmm));
        }

        long id;
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using var insertRoutine = connection.CreateCommand();
            insertRoutine.Transaction = transaction;
            insertRoutine.CommandText = """
                INSERT INTO routines (name, steps_json, created_utc)
                VALUES ($name, $steps, $created);
                """;
            insertRoutine.Parameters.AddWithValue("$name", name.Trim());
            insertRoutine.Parameters.AddWithValue("$steps", stepsJson);
            insertRoutine.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insertRoutine.ExecuteNonQuery();
            id = GetLastInsertRowId(connection);

            using var insertAction = connection.CreateCommand();
            insertAction.Transaction = transaction;
            insertAction.CommandText = """
                INSERT INTO scheduled_actions
                    (description, tool_name, args_json, trigger_utc, cron_expr)
                VALUES ($description, 'Routine.Run', $args, $trigger, $cron);
                """;
            insertAction.Parameters.AddWithValue("$description", name.Trim());
            insertAction.Parameters.AddWithValue("$args", System.Text.Json.JsonSerializer.Serialize(new { routine_id = id }));
            insertAction.Parameters.AddWithValue("$trigger", AsUtc(nextTriggerUtc).ToString("O", CultureInfo.InvariantCulture));
            insertAction.Parameters.AddWithValue("$cron", $"daily:{timeHhmm}");
            insertAction.ExecuteNonQuery();

            using var updateRoutine = connection.CreateCommand();
            updateRoutine.Transaction = transaction;
            updateRoutine.CommandText = "UPDATE routines SET scheduled_action_id = last_insert_rowid() WHERE id = $id;";
            updateRoutine.Parameters.AddWithValue("$id", id);
            updateRoutine.ExecuteNonQuery();
            transaction.Commit();
        }

        ScheduledActionsChanged?.Invoke();
        return id;
    }

    public bool SetRoutineEnabled(long id, bool isEnabled)
    {
        int changed;
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using var updateRoutine = connection.CreateCommand();
            updateRoutine.Transaction = transaction;
            updateRoutine.CommandText = "UPDATE routines SET is_enabled = $enabled WHERE id = $id;";
            updateRoutine.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
            updateRoutine.Parameters.AddWithValue("$id", id);
            changed = updateRoutine.ExecuteNonQuery();
            if (changed > 0)
            {
                using var updateAction = connection.CreateCommand();
                updateAction.Transaction = transaction;
                updateAction.CommandText = """
                    UPDATE scheduled_actions SET is_enabled = $enabled
                    WHERE id = (SELECT scheduled_action_id FROM routines WHERE id = $id);
                    """;
                updateAction.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
                updateAction.Parameters.AddWithValue("$id", id);
                updateAction.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        if (changed > 0)
        {
            ScheduledActionsChanged?.Invoke();
        }

        return changed > 0;
    }

    public long ScheduleRoutineNow(long id)
    {
        Routine routine = GetRoutine(id)
            ?? throw new InvalidOperationException($"Routine {id} was not found.");
        return AddScheduledAction(
            routine.Name,
            "Routine.Run",
            System.Text.Json.JsonSerializer.Serialize(new { routine_id = id }),
            DateTime.UtcNow);
    }

    public bool DeleteRoutine(long id)
    {
        int changed;
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using var deleteAction = connection.CreateCommand();
            deleteAction.Transaction = transaction;
            deleteAction.CommandText = """
                DELETE FROM scheduled_actions
                WHERE tool_name = 'Routine.Run'
                  AND args_json = $args;
                """;
            deleteAction.Parameters.AddWithValue(
                "$args",
                System.Text.Json.JsonSerializer.Serialize(new { routine_id = id }));
            deleteAction.ExecuteNonQuery();

            using var deleteRoutine = connection.CreateCommand();
            deleteRoutine.Transaction = transaction;
            deleteRoutine.CommandText = "DELETE FROM routines WHERE id = $id;";
            deleteRoutine.Parameters.AddWithValue("$id", id);
            changed = deleteRoutine.ExecuteNonQuery();
            transaction.Commit();
        }

        if (changed > 0)
        {
            ScheduledActionsChanged?.Invoke();
        }

        return changed > 0;
    }

    // Scheduled Actions CRUD

    public long AddScheduledAction(
        string description,
        string toolName,
        string argsJson,
        DateTime triggerUtc,
        string? cronExpression = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(argsJson);

        long id;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO scheduled_actions (description, tool_name, args_json, trigger_utc, cron_expr)
                VALUES ($description, $toolName, $argsJson, $triggerUtc, $cronExpression);
                """;
            command.Parameters.AddWithValue("$description", description.Trim());
            command.Parameters.AddWithValue("$toolName", toolName.Trim());
            command.Parameters.AddWithValue("$argsJson", argsJson);
            command.Parameters.AddWithValue("$triggerUtc", AsUtc(triggerUtc).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$cronExpression", (object?)cronExpression ?? DBNull.Value);
            command.ExecuteNonQuery();
            id = GetLastInsertRowId(connection);
        }

        ScheduledActionsChanged?.Invoke();
        return id;
    }

    public ScheduledAction? GetScheduledAction(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, description, tool_name, args_json, trigger_utc, cron_expr, is_enabled
            FROM scheduled_actions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadScheduledAction(reader) : null;
    }

    public IReadOnlyList<ScheduledAction> GetDueActions(DateTime? nowUtc = null)
    {
        string now = AsUtc(nowUtc ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, description, tool_name, args_json, trigger_utc, cron_expr, is_enabled
            FROM scheduled_actions
            WHERE is_enabled = 1 AND trigger_utc <= $now
            ORDER BY trigger_utc, id;
            """;
        command.Parameters.AddWithValue("$now", now);

        var results = new List<ScheduledAction>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadScheduledAction(reader));
        }

        return results;
    }

    public ScheduledAction? GetNextScheduledAction()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, description, tool_name, args_json, trigger_utc, cron_expr, is_enabled
            FROM scheduled_actions
            WHERE is_enabled = 1
            ORDER BY trigger_utc, id
            LIMIT 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadScheduledAction(reader) : null;
    }

    public IReadOnlyList<ScheduledAction> GetUpcomingActions(int limit = 50, DateTime? nowUtc = null)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        }

        string now = AsUtc(nowUtc ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, description, tool_name, args_json, trigger_utc, cron_expr, is_enabled
            FROM scheduled_actions
            WHERE is_enabled = 1 AND trigger_utc >= $now
            ORDER BY trigger_utc, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ScheduledAction>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadScheduledAction(reader));
        }

        return results;
    }

    public bool MarkExecutedOrReschedule(long id, DateTime? nextTriggerUtc = null)
    {
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            if (nextTriggerUtc is DateTime next)
            {
                command.CommandText = "UPDATE scheduled_actions SET trigger_utc = $triggerUtc WHERE id = $id;";
                command.Parameters.AddWithValue("$triggerUtc", AsUtc(next).ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                command.CommandText = "UPDATE scheduled_actions SET is_enabled = 0 WHERE id = $id;";
            }

            command.Parameters.AddWithValue("$id", id);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            ScheduledActionsChanged?.Invoke();
        }

        return changed > 0;
    }

    public bool UpdateScheduledAction(
        long id,
        string description,
        string toolName,
        string argsJson,
        DateTime triggerUtc,
        string? cronExpression = null,
        bool isEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(argsJson);

        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE scheduled_actions
                SET description = $description,
                    tool_name = $toolName,
                    args_json = $argsJson,
                    trigger_utc = $triggerUtc,
                    cron_expr = $cronExpression,
                    is_enabled = $isEnabled
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$description", description.Trim());
            command.Parameters.AddWithValue("$toolName", toolName.Trim());
            command.Parameters.AddWithValue("$argsJson", argsJson);
            command.Parameters.AddWithValue("$triggerUtc", AsUtc(triggerUtc).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$cronExpression", (object?)cronExpression ?? DBNull.Value);
            command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            ScheduledActionsChanged?.Invoke();
        }

        return changed > 0;
    }

    public bool DeleteScheduledAction(long id)
    {
        int changed;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM scheduled_actions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            ScheduledActionsChanged?.Invoke();
        }

        return changed > 0;
    }

    // Daily Stats & Clipboard

    public void IncrementStat(string metric, long amount = 1, DateTime? dateUtc = null)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount cannot be negative.");
        }

        string column = metric switch
        {
            "dictated_words" => "dictated_words",
            "voice_commands" => "voice_commands",
            "tools_executed" => "tools_executed",
            _ => throw new ArgumentOutOfRangeException(nameof(metric), "Unknown daily stat metric.")
        };
        string dateKey = AsUtc(dateUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO stats_daily (date_key, {column}) VALUES ($dateKey, $amount)
            ON CONFLICT(date_key) DO UPDATE SET {column} = {column} + excluded.{column};
            """;
        command.Parameters.AddWithValue("$dateKey", dateKey);
        command.Parameters.AddWithValue("$amount", amount);
        command.ExecuteNonQuery();
    }

    public DailyStats GetTodayStats(DateTime? dateUtc = null)
    {
        string dateKey = AsUtc(dateUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT date_key, dictated_words, voice_commands, tools_executed FROM stats_daily WHERE date_key = $dateKey;";
        command.Parameters.AddWithValue("$dateKey", dateKey);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new DailyStats(dateKey, 0, 0, 0);
        }

        return new DailyStats(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    public IReadOnlyList<ClipEntry> GetClipboardEntries(int limit = DefaultClipboardLimit)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The unpinned clipboard limit must be between 1 and 200.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, content_type, content, preview_text, is_pinned, created_utc
            FROM clipboard_entries
            WHERE is_pinned = 1 OR id IN (
                SELECT id FROM clipboard_entries
                WHERE is_pinned = 0
                ORDER BY created_utc DESC, id DESC
                LIMIT $limit
            )
            ORDER BY is_pinned DESC, created_utc DESC, id DESC
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<ClipEntry>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            byte[] protectedContent = (byte[])reader[2];
            byte[]? content = null;
            try
            {
                content = ProtectedData.Unprotect(protectedContent, null, DataProtectionScope.CurrentUser);
                string contentType = reader.GetString(1);
                string text = contentType == "image/png"
                    ? Convert.ToBase64String(content)
                    : Encoding.UTF8.GetString(content);
                string preview = contentType == "image/png"
                    ? $"Image · {content.Length / 1024:N0} KB"
                    : text.Length > 300 ? text[..300] : text;
                results.Add(new ClipEntry(
                    reader.GetInt64(0),
                    contentType,
                    text,
                    preview,
                    reader.GetInt64(4) != 0,
                    ParseUtc(reader.GetString(5))));
            }
            finally
            {
                if (content is not null)
                {
                    CryptographicOperations.ZeroMemory(content);
                }

                CryptographicOperations.ZeroMemory(protectedContent);
            }
        }

        return results;
    }

    public long AddClipboardText(string text, int maxEntries = DefaultClipboardLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (maxEntries is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Maximum unpinned entries must be between 1 and 200.");
        }

        return AddClipboardContent("text", Encoding.UTF8.GetBytes(text), maxEntries);
    }

    public long AddClipboardImage(byte[] pngBytes, int maxEntries = DefaultClipboardLimit)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length is 0 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(pngBytes), "PNG image data must be between 1 byte and 16 MB.");
        }

        if (maxEntries is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Maximum unpinned entries must be between 1 and 200.");
        }

        return AddClipboardContent("image/png", pngBytes.ToArray(), maxEntries);
    }

    private long AddClipboardContent(string contentType, byte[] content, int maxEntries)
    {
        byte[] protectedContent;
        try
        {
            protectedContent = ProtectedData.Protect(
                content,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(content);
            throw;
        }

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            DateTime nowUtc = DateTime.UtcNow;
            long? existingTextId = null;

            if (contentType == "text")
            {
                using var findDuplicate = connection.CreateCommand();
                findDuplicate.Transaction = transaction;
                findDuplicate.CommandText = """
                    SELECT id, content, created_utc
                    FROM clipboard_entries
                    WHERE content_type = 'text'
                    ORDER BY is_pinned DESC, created_utc DESC, id DESC;
                    """;

                using SqliteDataReader reader = findDuplicate.ExecuteReader();
                while (reader.Read())
                {
                    byte[] existingProtectedContent = (byte[])reader[1];
                    byte[]? existingContent = null;
                    try
                    {
                        existingContent = ProtectedData.Unprotect(
                            existingProtectedContent,
                            optionalEntropy: null,
                            DataProtectionScope.CurrentUser);
                        if (existingContent.Length == content.Length &&
                            CryptographicOperations.FixedTimeEquals(existingContent, content))
                        {
                            existingTextId = reader.GetInt64(0);
                            DateTime existingCreatedUtc = ParseUtc(reader.GetString(2));
                            if (nowUtc <= existingCreatedUtc)
                            {
                                nowUtc = existingCreatedUtc.AddTicks(1);
                            }

                            break;
                        }
                    }
                    finally
                    {
                        if (existingContent is not null)
                        {
                            CryptographicOperations.ZeroMemory(existingContent);
                        }

                        CryptographicOperations.ZeroMemory(existingProtectedContent);
                    }
                }
            }

            long id;
            string createdUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
            if (existingTextId is long matchingId)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE clipboard_entries SET created_utc = $createdUtc WHERE id = $id;";
                update.Parameters.AddWithValue("$createdUtc", createdUtc);
                update.Parameters.AddWithValue("$id", matchingId);
                update.ExecuteNonQuery();
                id = matchingId;
            }
            else
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO clipboard_entries (content_type, content, preview_text, created_utc)
                    VALUES ($contentType, $content, '', $createdUtc);
                    """;
                insert.Parameters.AddWithValue("$contentType", contentType);
                insert.Parameters.Add("$content", SqliteType.Blob).Value = protectedContent;
                insert.Parameters.AddWithValue("$createdUtc", createdUtc);
                insert.ExecuteNonQuery();
                id = GetLastInsertRowId(connection);
            }

            TrimUnpinnedClipboardEntries(connection, transaction, maxEntries);
            transaction.Commit();
            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            CryptographicOperations.ZeroMemory(protectedContent);
        }
    }

    public bool SetClipboardPinned(long id, bool isPinned, int maxUnpinned = DefaultClipboardLimit)
    {
        if (maxUnpinned is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUnpinned), "Maximum unpinned entries must be between 1 and 200.");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE clipboard_entries
            SET is_pinned = $isPinned,
                created_utc = CASE
                    WHEN is_pinned = 1 AND $isPinned = 0 THEN $createdUtc
                    ELSE created_utc
                END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$isPinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        bool changed = command.ExecuteNonQuery() > 0;
        if (!isPinned)
        {
            TrimUnpinnedClipboardEntries(connection, transaction, maxUnpinned);
        }

        transaction.Commit();
        return changed;
    }

    public bool SetClipboardEntryPinned(long id, bool isPinned) =>
        SetClipboardPinned(id, isPinned, DefaultClipboardLimit);

    public bool DeleteClipboardEntry(long id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM clipboard_entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        bool deleted = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return deleted;
    }

    public int ClearUnpinnedClipboardEntries()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM clipboard_entries WHERE is_pinned = 0;";
        int deleted = command.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    }

    public DateTimeOffset? GetCountdownTimerEndUtc()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT end_utc FROM countdown_timer WHERE id = 1;";
        object? value = command.ExecuteScalar();
        return value is string text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()
            : null;
    }

    public void SetCountdownTimerEndUtc(DateTimeOffset endUtc)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO countdown_timer (id, end_utc) VALUES (1, $endUtc)
            ON CONFLICT(id) DO UPDATE SET end_utc = excluded.end_utc;
            """;
        command.Parameters.AddWithValue("$endUtc", endUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void ClearCountdownTimer()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM countdown_timer WHERE id = 1;";
        command.ExecuteNonQuery();
    }

    private static void TrimUnpinnedClipboardEntries(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int maxUnpinnedEntries)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM clipboard_entries
            WHERE is_pinned = 0
              AND id NOT IN (
                  SELECT id FROM clipboard_entries
                  WHERE is_pinned = 0
                  ORDER BY created_utc DESC, id DESC
                  LIMIT $limit
              );
            """;
        command.Parameters.AddWithValue("$limit", maxUnpinnedEntries);
        command.ExecuteNonQuery();
    }

    // Audit Log

    public void AddAuditEntry(string toolName, string argumentsJson, string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_log (tool_name, arguments_json, outcome, created_utc)
            VALUES ($toolName, $argumentsJson, $outcome, $createdUtc);
            """;
        command.Parameters.AddWithValue("$toolName", toolName.Trim());
        command.Parameters.AddWithValue("$argumentsJson", argumentsJson ?? string.Empty);
        command.Parameters.AddWithValue("$outcome", outcome ?? string.Empty);
        command.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> GetRecentAuditEntries(int limit = 50)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT created_utc, tool_name, arguments_json, outcome
            FROM audit_log
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var list = new List<AuditEntry>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AuditEntry(
                ParseUtc(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return list;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA mmap_size=0;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS memories (
                id INTEGER PRIMARY KEY,
                fact TEXT NOT NULL,
                tags TEXT,
                created_utc TEXT NOT NULL
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts
            USING fts5(fact, tags, content='memories', content_rowid='id');

            CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
                INSERT INTO memories_fts(rowid, fact, tags) VALUES (new.id, new.fact, new.tags);
            END;

            CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
                INSERT INTO memories_fts(memories_fts, rowid, fact, tags)
                VALUES ('delete', old.id, old.fact, old.tags);
                INSERT INTO memories_fts(rowid, fact, tags) VALUES (new.id, new.fact, new.tags);
            END;

            CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
                INSERT INTO memories_fts(memories_fts, rowid, fact, tags)
                VALUES ('delete', old.id, old.fact, old.tags);
            END;

            CREATE TABLE IF NOT EXISTS todos (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                due_time TEXT,
                is_completed INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS scheduled_actions (
                id INTEGER PRIMARY KEY,
                description TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                args_json TEXT NOT NULL,
                trigger_utc TEXT NOT NULL,
                cron_expr TEXT,
                is_enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS routines (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                steps_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                scheduled_action_id INTEGER
            );

            CREATE TABLE IF NOT EXISTS stats_daily (
                date_key TEXT PRIMARY KEY,
                dictated_words INTEGER NOT NULL DEFAULT 0,
                voice_commands INTEGER NOT NULL DEFAULT 0,
                tools_executed INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS clipboard_entries (
                id INTEGER PRIMARY KEY,
                content_type TEXT NOT NULL,
                content BLOB NOT NULL,
                preview_text TEXT NOT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS countdown_timer (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                end_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY,
                tool_name TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                outcome TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );

            UPDATE clipboard_entries SET preview_text = '' WHERE preview_text <> '';
            """;
        command.ExecuteNonQuery();

        // One-time-in-effect migration: apply the 10-unpinned-item policy to existing databases.
        using var transaction = connection.BeginTransaction();
        TrimUnpinnedClipboardEntries(connection, transaction, DefaultClipboardLimit);
        transaction.Commit();
    }

    private static long GetLastInsertRowId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
        return connection;
    }

    private static MemoryEntry ReadMemory(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        ParseUtc(reader.GetString(3)));

    private static ScheduledAction ReadScheduledAction(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        ParseUtc(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt64(6) != 0);

    private static Routine ReadRoutine(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        ParseUtc(reader.GetString(3)),
        reader.GetInt64(4) != 0);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
