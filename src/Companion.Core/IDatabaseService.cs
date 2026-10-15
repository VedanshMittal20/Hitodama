namespace Companion.Core;

public interface IDatabaseService
{
    event Action? ScheduledActionsChanged;
    event Action? TodosChanged;
    event Action? MemoriesChanged;

    // Memory CRUD
    long AddMemory(string fact, string? tags = null);
    long InsertMemory(string fact, string? tags = null);
    MemoryEntry? GetMemory(long id);
    IReadOnlyList<MemoryEntry> GetAllMemories();
    IReadOnlyList<MemoryEntry> SearchMemoriesFts(string query, int limit = 10);
    bool UpdateMemory(long id, string fact, string? tags = null);
    bool DeleteMemory(long id);

    // Todo CRUD
    long AddTodo(string title, string? dueTimeText = null);
    IReadOnlyList<TodoItem> GetTodos();
    IReadOnlyList<TodoItem> GetActiveTodos();
    TodoItem? GetTodo(long id);
    bool ToggleTodo(long id);
    bool UpdateTodo(long id, string title, string? dueTimeText = null);
    bool UpdateTodo(long id, string title, string? dueTimeText, bool isCompleted);
    bool DeleteTodo(long id);

    // Scheduled Actions CRUD
    long AddScheduledAction(string description, string toolName, string argsJson, DateTime triggerUtc, string? cronExpression = null);
    ScheduledAction? GetScheduledAction(long id);
    IReadOnlyList<ScheduledAction> GetDueActions(DateTime? nowUtc = null);
    ScheduledAction? GetNextScheduledAction();
    IReadOnlyList<ScheduledAction> GetUpcomingActions(int limit = 50, DateTime? nowUtc = null);
    bool MarkExecutedOrReschedule(long id, DateTime? nextTriggerUtc = null);
    bool UpdateScheduledAction(long id, string description, string toolName, string argsJson, DateTime triggerUtc, string? cronExpression = null, bool isEnabled = true);
    bool DeleteScheduledAction(long id);

    // Routines CRUD
    long AddRoutine(string name, string stepsJson, DateTime nextTriggerUtc, string timeHhmm);
    IReadOnlyList<Routine> GetRoutines();
    Routine? GetRoutine(long id);
    bool SetRoutineEnabled(long id, bool isEnabled);
    long ScheduleRoutineNow(long id);
    bool DeleteRoutine(long id);

    // Stats & Clipboard
    void IncrementStat(string metric, long amount = 1, DateTime? dateUtc = null);
    DailyStats GetTodayStats(DateTime? dateUtc = null);
    IReadOnlyList<ClipEntry> GetClipboardEntries(int limit = 10);
    long AddClipboardText(string text, int maxEntries = 10);
    long AddClipboardImage(byte[] pngBytes, int maxEntries = 10);
    bool SetClipboardPinned(long id, bool isPinned, int maxUnpinned = 10);
    bool SetClipboardEntryPinned(long id, bool isPinned);
    bool DeleteClipboardEntry(long id);
    int ClearUnpinnedClipboardEntries();

    // Persistent countdown timer
    DateTimeOffset? GetCountdownTimerEndUtc();
    void SetCountdownTimerEndUtc(DateTimeOffset endUtc);
    void ClearCountdownTimer();

    // Audit Log
    void AddAuditEntry(string toolName, string argumentsJson, string outcome);
    IReadOnlyList<AuditEntry> GetRecentAuditEntries(int limit = 50);
}
