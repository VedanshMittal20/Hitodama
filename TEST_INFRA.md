# Void AI — Test Infrastructure (`TEST_INFRA.md`)

This document defines the architecture, conventions, execution commands, and guidelines for the automated test suite of **Void AI**.

---

## 1. Test Framework Stack

| Component | Technology | Version | Purpose |
|---|---|---|---|
| **Test Runner & Framework** | xUnit | 2.9.3 | Modern, parallel-capable testing framework for .NET |
| **Assertion Library** | FluentAssertions | 8.11.0 | Readable, type-safe assertions with rich error diagnostics |
| **SDK Runner** | Microsoft.NET.Test.Sdk | 17.14.1 | Visual Studio & CLI test host bridge |
| **Visual Studio Adapter** | xunit.runner.visualstudio | 3.1.4 | Test discovery and execution adapter |
| **Target Framework** | `net8.0-windows10.0.19041.0` | 8.0 | Matches solution target framework with WPF & WinForms integration |
| **Runtime RollForward** | `Major` | — | Guarantees test runner compatibility across .NET 8 / 10 hosts |

---

## 2. Solution & Project Organization

The test suite resides in a dedicated test project added to `Companion.sln`:

```
c:\Users\vedan\OneDrive\Desktop\Void\
├── Companion.sln
├── src\
│   ├── Companion.Core\
│   ├── Companion.Services\
│   ├── Companion.Shell\
│   └── Companion.Studio\
└── tests\
    └── Companion.Tests\
        ├── Companion.Tests.csproj
        ├── TestInfrastructure\
        │   ├── TestDirectoryScope.cs
        │   └── GatekeeperContracts.cs
        ├── Tier1_FeatureCoverage\
        │   ├── SecretStoreTests.cs
        │   ├── DatabaseServiceTests.cs
        │   ├── IpcProtocolTests.cs
        │   ├── GatekeeperTests.cs
        │   ├── SpringSolverTests.cs
        │   ├── IslandTabsModelsTests.cs
        │   ├── GlobalInputChordTests.cs
        │   ├── AudioBufferTests.cs
        │   └── AutonomousSchedulerTests.cs
        ├── Tier2_BoundaryCornerCases\
        │   ├── SecretStoreBoundaryTests.cs
        │   ├── DatabaseBoundaryTests.cs
        │   ├── IpcBoundaryTests.cs
        │   ├── GlobalInputBoundaryTests.cs
        │   ├── AudioBufferBoundaryTests.cs
        │   └── SchedulerBoundaryTests.cs
        ├── Tier3_CrossFeatureCombinations\
        │   ├── IpcGatekeeperToolCombinationsTests.cs
        │   ├── SchedulerDatabaseNotificationCombinationsTests.cs
        │   ├── SpringSolverWindowBoundsCombinationsTests.cs
        │   └── AudioBargeInFlameCombinationsTests.cs
        └── Tier4_RealWorldScenarios\
            ├── ColdStartOnboardingScenarioTests.cs
            ├── CircleToAskScenarioTests.cs
            ├── TalkBadlyCleanDictationScenarioTests.cs
            ├── DailyRoutineTriggerScenarioTests.cs
            └── StudioLoadoutLiveUpdateScenarioTests.cs
```

---

## 3. Four-Tier Test Methodology

The test suite is structured into four distinct tiers:

### Tier 1: Feature Coverage (>=5 test cases per feature area)
- **SecretStore (R1)**: DPAPI encryption/decryption roundtrip, key deletion, overwrite, null return when absent, empty string guard, cryptographic isolation.
- **DatabaseService WAL/FTS5 (R1)**: WAL pragma verification, table schema creation, memories insert & FTS5 search, delete triggers sync, todos CRUD, scheduled actions queries, daily stats accumulation, clipboard encrypted storage.
- **JSON-RPC IPC (R1)**: JSON-RPC 2.0 request/response/notification formats, error responses, contract method dispatching (`loadout.reload`, `shell.getState`, `shell.setTab`, `gatekeeper.respond`), client connection timeout.
- **Gatekeeper Security Engine (R1)**: Low-risk auto approval in trusted context, medium/high risk user confirmation, untrusted context promotion (escalating low-risk to confirmation), persistent untrusted context tracking.
- **SpringSolver (R2)**: Initial state, displacement toward target, 120Hz sub-stepping stability, automatic detachment when displacement & velocity drop below $\epsilon = 0.05$, zero displacement immediate settling.
- **7 Island Tabs Models (R3)**: Tab 1 (Controls), Tab 2 (Today), Tab 3 (Stats Bento with WPM advantage & time saved), Tab 4 (Clipboard), Tab 5 (Apps launcher), Tab 6 (Wallpaper), Tab 7 (Power), and 4-Slot Soul Loadout.
- **GlobalInputService Chord Logic (R4)**: Chord threshold durations (Ctrl+Shift >180ms, Ctrl+Win >150ms), modifier key classification, hotkey definitions (Assistant, Kill Switch), rapid cancellation on non-modifier press.
- **VAD/Audio Buffers (R5)**: AudioPlayerService 24kHz buffer queuing, instant barge-in flush, silence RMS level (0.0), voice RMS detection (>0.025), 18-chunk (720ms) hangover VAD.
- **AutonomousScheduler (R6)**: Immediate due action execution, invalid start exception, failure handling via `ActionFailed`, daily recurring routine reschedule (`daily:HH:mm`), zero-polling `Wake()` dynamic trigger.

### Tier 2: Boundary & Corner Cases
- **SecretStore**: Corrupt DPAPI cipher bytes, 100KB huge token payload, multi-threaded DPAPI protection/unprotection without data corruption, invalid whitespace inputs.
- **DatabaseService**: FTS5 adversarial injection queries (`"""""`, `*`, `AND OR NOT NEAR`, `(foo OR bar)`, emojis), 1MB memory text payload, concurrent WAL multi-threaded reads/writes, limit boundaries ($0, -1, 101$), clipboard maxEntries clamping ($1$), negative stat increments.
- **IPC / JSON-RPC**: 500KB huge payload arguments, malformed JSON, empty method name, deeply nested parameter structures (10 levels).
- **GlobalInput**: Sub-threshold rapid key taps (<50ms), non-modifier interruption during hold, reverse modifier release order, multiple simultaneous modifiers.
- **Audio Buffers**: Buffer overflow handling (1,000,000 bytes exceeding 15-second capacity), rapid flush/enqueue alternation (50 iterations), extreme clipping clamping ($1.0$), odd byte length alignment safety.
- **AutonomousScheduler**: Malformed cron expressions (`daily:`, `daily:invalid`, `daily:25:99`, `* * * * *`), far-past timestamps (year 2020), far-future timestamps (year 2099), concurrent `Wake()` calls from 50 threads.

### Tier 3: Cross-Feature Combinations
- **IPC + Gatekeeper + Tool Execution**: End-to-end tool invocation over IPC, Gatekeeper risk classification, untrusted context escalation, confirmation dialog integration, and SQLite audit stats increment.
- **Scheduler + Database + Notifications**: Scheduled action triggers -> scheduler wakes via `ScheduledActionsChanged` -> executes tool -> updates `DailyStats` -> reschedules daily routine.
- **SpringSolver + Window Bounds**: 120Hz physics solver drives Island dock expansion ($164 \times 36 \to 640 \times 390$) and collapse ($640 \times 390 \to 164 \times 36$) while enforcing symmetric horizontal centering ($X = (1920 - W)/2$) and top anchoring ($Y = 0$).
- **Audio Capture + Barge-in Flush + Flame RMS**: Multi-amplitude RMS calculations drive spirit flame intensity; assistant speech playback is immediately cleared upon detected user interruption.

### Tier 4: Real-World Application Scenarios
- **Cold Start Onboarding**: Clean-slate startup -> API key check -> 3-step onboarding (Make -> Paste -> Talk) -> default 4-slot loadout -> SQLite WAL/FTS5 initialization.
- **Circle-to-Ask Pipeline**: Hold Ctrl+Shift (>180ms) -> Lasso selection crop -> Voice question capture -> Context marked untrusted -> Multimodal Gemini payload generation -> Response stored in FTS5 memory.
- **Talk Badly, Send Clean Dictation**: Hold Ctrl+Win (>150ms) -> Voice captured with false starts and self-corrections -> Cleaned text transcription -> Dictated words calculated -> Stats and clipboard ring buffer updated.
- **Daily Routine Trigger**: Morning routine configured with `daily:09:00` -> Zero-polling wake-up -> Reads active todos -> Records tool execution -> Auto-rescheduled for the following day.
- **Studio Loadout Live Update**: Studio process customizes 4-slot loadout -> Persists to JSON -> Dispatches IPC reload notification -> Shell synchronizes state live.

---

## 4. Test Execution Commands

### Run Entire Test Suite (Release)
```powershell
dotnet test -c Release
```

### Run Entire Test Suite (Debug)
```powershell
dotnet test
```

### Filter by Tier
```powershell
# Tier 1: Feature Coverage
dotnet test --filter "FullyQualifiedName~Tier1_FeatureCoverage"

# Tier 2: Boundary & Corner Cases
dotnet test --filter "FullyQualifiedName~Tier2_BoundaryCornerCases"

# Tier 3: Cross-Feature Combinations
dotnet test --filter "FullyQualifiedName~Tier3_CrossFeatureCombinations"

# Tier 4: Real-World Scenarios
dotnet test --filter "FullyQualifiedName~Tier4_RealWorldScenarios"
```

### Filter by Feature Area
```powershell
dotnet test --filter "FullyQualifiedName~SecretStore"
dotnet test --filter "FullyQualifiedName~Database"
dotnet test --filter "FullyQualifiedName~SpringSolver"
dotnet test --filter "FullyQualifiedName~AutonomousScheduler"
dotnet test --filter "FullyQualifiedName~AudioBuffer"
dotnet test --filter "FullyQualifiedName~Gatekeeper"
```

---

## 5. Isolation & Concurrency Guidelines

1. **Database Isolation**: Tests must never use the global application database (`companion.db`). Instead, each test creates an isolated temporary directory via `TestDirectoryScope` and creates a dedicated database file (`scope.CreateFilePath("test.db")`).
2. **SecretStore Serialization**: Tests touching DPAPI disk storage must belong to `[Collection("SecretStoreCollection")]` to prevent parallel file lock contention on Windows NTFS.
3. **Clean Disposal**: All background runners, schedulers, and audio providers implement `IDisposable` or `IAsyncDisposable` and are disposed in test fixtures.
