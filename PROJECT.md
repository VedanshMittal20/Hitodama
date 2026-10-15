# Project: Hitodama AI

## Architecture
Hitodama AI is a voice-first Windows desktop companion built in C# 12 and .NET 8 WPF (`net8.0-windows10.0.19041.0`).
The solution is organized into 4 cleanly decoupled projects:
1. **Companion.Core**: Fundamental data models, DPAPI secret storage, SQLite database with WAL and FTS5 full-text search triggers, JSON-RPC 2.0 NamedPipe IPC contracts, and Tool/Gatekeeper security interfaces.
2. **Companion.Services**: Domain services including 120Hz SpringSolver, NAudio 16kHz PCM capture with hangover VAD, 24kHz audio playback with barge-in flush, Gemini Live WebSocket client, Gemini Vision service, Low-level keyboard hook (WH_KEYBOARD_LL), System control (GSMTC, Wi-Fi, Bluetooth, Audio, Power), and AutonomousScheduler.
3. **Companion.Shell**: The non-activating, top-centered 720x520 transparent Island dock HWND, hardware-accelerated SoulOrb flame visualizer, 7 island tabs, Circle-to-Ask screen lasso overlay, and Gatekeeper confirmation UI.
4. **Companion.Studio**: On-demand WPF configuration suite (0 MB idle RAM when closed) for 4-slot soul loadout customization, 3-step API key onboarding, FTS5 memory browser, and audit log viewer. Communicates with Shell via `companion-ipc`.

```
               ┌────────────────────────────────────────────────────────┐
               │                     User / Desktop                     │
               └───────────▲──────────────────────────────▲────────────┘
                           │ (Voice / Hotkeys / Gestures) │ (Settings)
               ┌───────────▼──────────────────┐   ┌───────▼────────────┐
               │       Companion.Shell        │   │  Companion.Studio  │
               │  - IslandWindow (720x520)    │   │  - 4-Slot Loadout  │
               │  - 7 Island Tabs             │   │  - Key Onboarding  │
               │  - SoulOrbControl (Flame)    │   │  - FTS5 Memory     │
               │  - CircleToAskOverlay        │   │  - Audit Log       │
               │  - Gatekeeper Dialog         │   │  (On-Demand / 0MB) │
               └───────────▲──────────────────┘   └───────▲────────────┘
                           │                              │
                           │   JSON-RPC (companion-ipc)   │
                           │◄─────────────────────────────┘
                           │
               ┌───────────▼──────────────────┐
               │      Companion.Services      │
               │  - SpringSolver (120Hz)      │
               │  - AudioPlayer / MicCapture  │
               │  - GeminiLiveService (Bidi)  │
               │  - GlobalInputService (Hook) │
               │  - SystemControlService      │
               │  - AutonomousScheduler       │
               │  - ToolFramework/Gatekeeper  │
               └───────────▲──────────────────┘
                           │
               ┌───────────▼──────────────────┐
               │        Companion.Core        │
               │  - SecretStore (DPAPI)       │
               │  - DatabaseService (WAL/FTS) │
               │  - IpcProtocol (JSON-RPC)    │
               │  - Models & Contracts        │
               └──────────────────────────────┘
```

## Code Layout
- `src/Companion.Core/`
  - `AppPaths.cs`: Standard app data directory resolution (`%LocalAppData%\Hitodama`).
  - `DatabaseService.cs`: SQLite connection, WAL mode, FTS5 table, sync triggers, CRUD operations, reactive events.
  - `SecretStore.cs`: DPAPI protected storage for Gemini API key and credentials.
  - `IpcProtocol.cs`: JSON-RPC 2.0 request/response/notification definitions and named pipe client/server (`companion-ipc`).
  - `Models.cs`: Soul loadout, Memory, Todo, ScheduledAction, DailyStats, ToolDefinition, ToolRiskLevel.
  - `Security/`: Gatekeeper security engine contracts and context tracking.
- `src/Companion.Services/`
  - `SpringSolver.cs`: Second-order harmonic physics solver for 120Hz smooth animations.
  - `MicCaptureService.cs`: NAudio 16kHz PCM capture, hangover VAD, RMS level calculation.
  - `AudioPlayerService.cs`: NAudio 24kHz PCM playback with instant barge-in buffer flush.
  - `GeminiLiveService.cs`: Bidirectional WebSocket client for Gemini Live BidiGenerateContent.
  - `GlobalInputService.cs`: WH_KEYBOARD_LL low-level keyboard hook with chord timers.
  - `SystemControlService.cs`: WinRT GSMTC media session, Wi-Fi, Bluetooth, Audio endpoints, Power actions.
  - `AutonomousScheduler.cs`: Zero-polling AutoResetEvent scheduler for delayed tasks & daily routines.
  - `Tools/`: Tool execution framework and Gatekeeper untrusted context tracker.
  - `DictationService.cs`: Audio capture and false-start cleanup via Gemini Flash.
  - `GeminiVisionService.cs`: Multimodal image + audio inference for Circle to Ask.
- `src/Companion.Shell/`
  - `App.xaml / App.xaml.cs`: Application lifecycle, single-instance mutex, tray icon, 2-minute idle memory trim.
  - `Windows/IslandWindow.xaml / .xaml.cs`: Non-activating 720x520 window, hit-testing, tab switching, dock animation.
  - `Windows/CircleToAskOverlay.xaml / .xaml.cs`: Fullscreen transparent lasso trail, crop, query.
  - `Controls/SoulOrbControl.cs`: Multi-wave spirit flame rendering via CompositionTarget.Rendering.
  - `Controls/EqualizerBars.xaml`: 4-bar dynamic music equalizer.
  - `Tabs/`: User controls for Controls, Today, Stats, Clipboard, Apps, Wallpaper, Power.
- `src/Companion.Studio/`
  - `App.xaml / App.xaml.cs`: Standalone on-demand WPF application lifecycle.
  - `MainWindow.xaml / .xaml.cs`: 4-Slot Loadout editor, 3-step API key onboarding, FTS5 browser, Audit log viewer.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | 4-Project Clean Solution Scaffolding | Companion.Core, Services, Shell, Studio target net8.0-windows10.0.19041.0 | M1 | R1 |
| 2 | DPAPI SecretStore | Windows DataProtectionScope.CurrentUser protection for Gemini API key | M1 | R1 |
| 3 | SQLite WAL Mode & FTS5 | DatabaseService with WAL mode, FTS5 virtual table, sync triggers, full CRUD | M1 | R1 |
| 4 | JSON-RPC 2.0 NamedPipe IPC | Zero-polling NamedPipe server/client over `companion-ipc` for Shell & Studio | M1 | R1 |
| 5 | Tool Framework & Gatekeeper Engine | Tool abstraction, untrusted context tracking, confirmation dialogs for medium/high risk | M1 | R1 |
| 6 | 2nd-Order Harmonic SpringSolver | 120Hz sub-stepping solver, detaching below epsilon=0.05 | M2 | R2 |
| 7 | Non-Activating Island Shell HWND | 720x520 fixed HWND, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE, Top=0 centered | M2 | R2 |
| 8 | Win32 Hit-Testing Passthrough | WM_NCHITTEST HTTRANSPARENT (-1) outside pill bounds | M2 | R2 |
| 9 | Mouse Wheel Scroll & Flick Gestures | Scroll down/flick to expand (640x390), scroll up to collapse (164x36), horizontal tab switch | M2 | R2 |
| 10 | Fullscreen Auto-Hide Detection | SHQueryUserNotificationState + SetWinEventHook to auto-hide during games/video | M2 | R2 |
| 11 | Shell Bug Fixes | Fix WM_MOUSEHWHEEL wParam delta extraction and ExpandedContent opacity | M2 | R2 |
| 12 | Tab 1: Controls & Music Island GSMTC | Wi-Fi SSID, Bluetooth toggle, Audio slider, Night Light, Dark Mode, GSMTC glow + 4-bar EQ | M3 | R3 |
| 13 | Tab 2: Today Tasks & Routines | To-Do list with counters (0/3), scheduled tasks timeline, daily routines with "Run Now" | M3 | R3 |
| 14 | Tab 3: Stats Bento Cards | Voice speed advantage (WPM & time saved), autonomous tasks executed, system vitals, Gemini session | M3 | R3 |
| 15 | Tab 4: Clipboard Ring Buffer | WM_CLIPBOARDUPDATE 200 items, password manager filtering, 1-click paste via SendInput | M3 | R3 |
| 16 | Tab 5: Apps Launcher | Start Menu .lnk + UWP fuzzy launcher, background indexing, 32x32 cached PNG icons | M3 | R3 |
| 17 | Tab 6: Wallpaper Switcher | WIC thumbnail loading (no file lock), SPI_SETDESKWALLPAPER desktop application | M3 | R3 |
| 18 | Tab 7: Power Tiles | Lock, Sleep, Restart, Shutdown, and Quiet Mode toggle | M3 | R3 |
| 19 | WH_KEYBOARD_LL Low-Level Hook | Low-level hook with hold timers, non-blocking queue, injected key filtering (LLKHF_INJECTED) | M4 | R4 |
| 20 | Circle to Ask Overlay | Hold Ctrl+Shift (>180ms), glowing dual-layer lasso, region crop + voice question, JPEG+PCM to Gemini | M4 | R4 |
| 21 | Clean Dictation | Hold Ctrl+Win (>150ms), 16kHz PCM capture, filler/false-start cleanup prompt, SendInput injection | M4 | R4 |
| 22 | Global Shortcuts & Emergency Kill Switch | Ctrl+Alt+Space (toggle voice / expand dock), Ctrl+Alt+Backspace (emergency abort all) | M4 | R4 |
| 23 | Gemini Live Bidi WebSocket Client | Bidirectional WebSocket connected to BidiGenerateContent with dynamic handshake | M5 | R5 |
| 24 | 4-Slot Soul Loadout Integration | Handshake incorporating Soul flame, Personality prompt, Voice preset, Abilities + FTS5 memories | M5 | R5 |
| 25 | NAudio 16kHz PCM Mic Capture & VAD | 16kHz capture, 720ms hangover VAD, RMS level calculation for flame reactivity | M5 | R5 |
| 26 | 24kHz Audio Playback & Barge-In Flush | Low-latency 24kHz PCM playback with instant buffer clearing on interruption | M5 | R5 |
| 27 | Hardware-Accelerated SoulOrbControl | Multi-wave living spirit flame rendering, 0% CPU when idle | M5 | R5 |
| 28 | Zero-Polling AutonomousScheduler | Single AutoResetEvent sleep-until-next-trigger runner for delayed actions and daily routines | M6 | R6 |
| 29 | On-Demand Companion.Studio Process | Standalone app for loadout customization, 3-step onboarding, memory browser, audit logs (0 MB idle RAM) | M6 | R6 |
| 30 | 2-Minute Idle Memory Trimming | GC collection and Win32 SetProcessWorkingSetSize trim after 2 minutes of idle | M6 | R6 |
| 31 | Automated Test Suite | 239 automated tests currently pass (100% pass rate) | M7 | Acceptance |
| 32 | Adversarial Hardening (Tier 5) | White-box stress-testing, fuzzing, and edge-case hardening | M7 | Acceptance |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Core Infrastructure & Security | R1: DPAPI SecretStore, SQLite WAL+FTS5 CRUD & events, JSON-RPC IPC over `companion-ipc`, Tool Framework & Gatekeeper security engine | none | Implemented; certified PASS |
| M2 | 120Hz Spring Physics & Shell Mechanics | R2: SpringSolver CompositionTarget detachment, Non-activating transparent HWND, WM_NCHITTEST passthrough, gestures, fullscreen auto-hide, Shell bug fixes | M1 | Implemented; verified PASS |
| M3 | Authentic 7 Hitodama Island Tabs & Offline Utilities | R3: Controls (GSMTC+Equalizer+WiFi+BT), Today, Stats Bento, Clipboard ring buffer+SendInput, Apps fuzzy launcher, Wallpaper WIC, Power tiles | M1, M2 | Implemented; verified PASS |
| M4 | Global Hotkeys, Circle to Ask & Clean Dictation | R4: WH_KEYBOARD_LL hook + timers, CircleToAskOverlay lasso crop + mic prompt, Clean Dictation SendInput, Ctrl+Alt+Space, Ctrl+Alt+Backspace kill switch | M1, M2 | Implemented; verified PASS |
| M5 | Real-Time Gemini Live Audio Pipeline & 4-Slot Soul Loadout | R5: Gemini Live Bidi WebSocket, 4-slot setup handshake + FTS5 memory, NAudio 16kHz VAD, 24kHz playback + instant barge-in flush, SoulOrbControl flame | M1, M2, M3 | Implemented; verified PASS |
| M6 | Autonomous Scheduler & On-Demand Studio Process | R6: Zero-polling AutonomousScheduler, Companion.Studio (onboarding, loadout, memory browser, audit log), 2-min idle memory trim | M1, M4, M5 | Implemented; verified PASS |
| M7 | E2E Integration, 100% Test Pass & Adversarial Hardening | Phase 1: E2E coverage (Tiers 1-4); Phase 2: Tier 5 adversarial hardening | M1–M6, TEST_READY | All 239 automated tests pass (100%); performance gate verified at 0.0% CPU and 37.1 MB RAM |

## Current Implementation Notes
- Gemini Live advertises tool definitions only for enabled ability packs, handles function-call responses, and routes execution through Gatekeeper. High-risk system actions use the tool's per-action assessed risk; tool calls and decisions are audited.
- Web-search grounding and visible active-window reads taint the session as untrusted. Enter/Escape can resolve an active permission prompt globally, and UI automation confirms the original foreground window is still active before acting.
- Daily routines are persisted and schedulable; text and PNG clipboard history are DPAPI-protected in SQLite. Wi-Fi and Bluetooth now have direct Controls-tab toggles; GSMTC provides album artwork and event-driven timeline progress when the media app exposes them. Circle to Ask sends its captured JPEG and 16 kHz PCM question into Gemini Live.
- Gatekeeper resolution counters are atomic and count each request once. Secret writes are serialized per destination, and reads permit atomic replacement without sharing violations.
- Known gaps: Night Light still opens Windows Display settings rather than toggling directly. Real Gemini credentials, audio devices, radios, and UI Automation targets still need desktop validation.
- The Windows x64 Shell and Studio are published self-contained, so their apphosts do not require a machine-wide .NET 8 install. Both launched directly; Studio displayed its main window. The final Shell passed the idle performance gate with `tools/verify-perf.ps1` at 0.0% average CPU and 37.1 MB peak private working set (well under the 85 MB limit).
- Latest automated verification: Release build succeeds with zero warnings/errors and all 239 tests pass (0 failed, 0 skipped). Hardware behavior, real Gemini credentials, audio capture/playback, and UI Automation targets still require desktop validation.

## Interface Contracts

### 1. NamedPipe JSON-RPC 2.0 (`companion-ipc`)
- **Transport**: `\\.\pipe\companion-ipc`
- **Format**: JSON-RPC 2.0
- **Methods**:
  - `loadout.reload` -> returns `{ "success": true }`
  - `loadout.get` -> returns current 4-slot loadout JSON
  - `loadout.set` (params: loadout object) -> updates loadout, notifies Shell
  - `gatekeeper.respond` (params: `{ "requestId": string, "approved": bool }`) -> handles permission dialog response
  - `shell.getState` -> returns `{ "islandState": string, "isLiveActive": bool, "currentTab": int }`
  - `shell.setTab` (params: `{ "tabIndex": int }`) -> switches active tab

### 2. Tool Framework & Gatekeeper Security Engine
- **Interface**: `ITool`, `IGatekeeperService`
- **Types**:
  ```csharp
  public enum ToolRiskLevel { Low, Medium, High }
  public record ToolDefinition(string Name, string Description, ToolRiskLevel RiskLevel, JsonDocument Schema);
  public record ToolExecutionContext(bool IsUntrustedInput, string SourceMessageId);
  public interface IGatekeeperService {
      Task<bool> RequestExecutionApprovalAsync(ToolDefinition tool, JsonElement arguments, ToolExecutionContext context);
      void TrackUntrustedContext(bool isUntrusted);
      bool IsContextUntrusted { get; }
  }
  ```

### 3. DatabaseService Reactive Contracts
- Events:
  - `event EventHandler? ScheduledActionsChanged;`
  - `event EventHandler? TodosChanged;`
  - `event EventHandler? MemoriesChanged;`
- CRUD methods:
  - `GetUpcomingActions()`, `AddScheduledAction()`, `DeleteScheduledAction()`
  - `GetActiveTodos()`, `AddTodo()`, `ToggleTodo()`, `DeleteTodo()`
  - `SearchMemoriesFts(query)`, `InsertMemory()`, `UpdateMemory()`, `DeleteMemory()`
  - `GetDailyStats(date)`, `IncrementStat(metric, delta)`

### 4. SpringSolver Physics Contract
- Formula: $x(t) = e^{-\zeta \omega t} (A \cos(\omega_d t) + B \sin(\omega_d t)) + x_{\text{target}}$
- Settling condition: $|x - x_{\text{target}}| < 0.05 \land |v| < 0.05$ -> detach `CompositionTarget.Rendering`.

### 5. Gemini Live & Soul Loadout Contract
- WebSocket endpoint: `wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent`
- Handshake payload:
  ```json
  {
    "setup": {
      "model": "models/gemini-2.5-flash-native-audio-preview-12-2025",
      "generationConfig": {
        "responseModalities": ["AUDIO"],
        "speechConfig": { "voiceConfig": { "prebuiltVoiceConfig": { "voiceName": "<VoiceName>" } } }
      },
      "systemInstruction": {
        "parts": [{ "text": "<PersonalityPrompt> + Memories" }]
      },
      "tools": [{ "functionDeclarations": [...] }]
    }
  }
  ```
- Audio In: 16kHz, 16-bit, Mono PCM, 100ms chunks (3200 bytes).
- Audio Out: 24kHz, 16-bit, Mono PCM.
- Interruption: `interrupted == true` -> immediate `_buffer.ClearBuffer()`.
