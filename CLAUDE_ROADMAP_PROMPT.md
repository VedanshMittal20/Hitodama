# Claude Prompt: Plan Void’s Dynamic Island + AI Roadmap

You are planning work on the existing Void AI repository. **Inspect the codebase and produce a practical implementation plan only. Do not edit or generate replacement source files.** The plan will be implemented later by Codex or Gemini Antigravity, so make it precise enough for another coding agent to follow.

## Product goal

Evolve Void into a polished, resource-light Windows desktop companion that combines:

- Apple Dynamic Island / Now Bar-style glanceable activities and transitions.
- A useful always-available clock, countdown timer, and other small desktop utilities.
- Galaxy One UI-inspired music visualization, especially album-art-aware color and smooth motion.
- Void’s own Soul Orb identity and Gemini AI capabilities: live voice conversation, Circle to Ask/search using a selected screen region and spoken question, clean dictation, Gemini tools, memories, research, and secure confirmations.

Use [Refero](https://styles.refero.design/), [Watermelon UI](https://ui.watermelon.sh/), [Cult UI](https://cult-ui.com/), Galaxy One UI music visualizers, and Apple’s Dynamic Island / Now Bar as inspiration. Translate ideas into native Windows WPF controls; do not port React components or add a web runtime. Keep Void’s own visual identity rather than copying another product.

## User-reported problems and desired behavior

1. **Circle to Ask/search is not working reliably.** Diagnose the actual failure path before prescribing a fix.
2. **Live speech is not working reliably.** Diagnose capture, connection, transcription, playback, lifecycle, and user-entry paths.
3. **Clipboard history should retain at most 10 recent unpinned copies, plus every item the user pins.** Pins must survive new copies and app restarts until explicitly unpinned/deleted.
4. **Music visualization should feel more dynamic and album-colored**, in the spirit of the newer Galaxy One UI visualizers.
5. **The island should work more like a Now Bar / Dynamic Island**, presenting the most useful current activity (Gemini voice, music, timer, clipboard feedback, scheduled/task activity, etc.) and expanding into a coherent AI/control surface. Keep a clock accessible when media is playing. Add timer support and plan how other activities can fit without the top pill becoming cluttered.
6. Keep it aesthetic while using few resources, especially when resident and idle.

## Repository facts found so far — verify these against the current checkout

- Native C# 12 / .NET 8 WPF solution with `Companion.Core`, `Companion.Services`, `Companion.Shell`, and `Companion.Studio`.
- Shell is a resident, single-instance, transparent 720×520 host. The visible island changes size inside the host. It is designed to preserve focus in the active app and pass pointer input through outside the island. Keep this behavior and avoid resizing the HWND every animation frame.
- Main dock XAML and code-behind are consolidated in `src/Companion.Shell/Windows/IslandWindow.xaml` and `.xaml.cs`. The current dock has Controls, Today, Stats, Clipboard, Apps, Wallpaper, and Power pages. Studio is a separate on-demand window with Soul, Connect & talk, and Memory & audit sections.
- Existing shell states include Collapsed (164×36), CompactLive (360×56), VoiceActive (440×180), PermissionPrompt (440×180), and ExpandedDock (640×390). The island uses an interruptible spring animator in `src/Companion.Services/SpringSolver.cs`, detaching its render callback when springs settle.
- `src/Companion.Shell/Windows/CircleToAskOverlay.xaml.cs` starts microphone recording when the overlay opens. On release it captures the lasso’s bounding rectangle (with padding), downsizes the longest side to at most 1280px, JPEG-encodes it, and returns it with 16kHz mono PCM. `IslandWindow.xaml.cs` then connects Gemini Live, sends the initial image/audio, and starts the live microphone. It requires a Gemini API key. This describes the current path, not a confirmed root cause.
- `src/Companion.Services/GeminiLiveService.cs` uses a WebSocket, sends 16kHz PCM chunks, receives input/output transcripts and audio, and raises session/interruption/failure events. `MicCaptureService.cs` uses NAudio at 16kHz mono PCM with a simple RMS threshold and hangover for audio chunks. Inspect ordering, races, device/permission failures, API/model compatibility, error surfacing, cancellation, and audio playback rather than assuming any one is the cause.
- **Potential entry-point mismatch to investigate:** `Ctrl+Alt+Space` raises `AssistantHotkeyPressed`; `OnAssistantHotkeyPressed` stops a live session if one exists, otherwise it only toggles the dock. No obvious Talk/Start Live Voice button appears in the island XAML. Confirm whether another path starts a normal live conversation and whether the UI/help text promises behavior the implementation does not provide.
- **Potential transcript UX issue to investigate:** Gemini Live forwards both input and output transcription through the same `TranscriptReceived` string event; the compact UI writes both to one subtitle. Consider whether this contributes to “not working properly.”
- Clipboard schema/model already has `is_pinned` / `ClipEntry.IsPinned`, but `DatabaseService.GetClipboardEntries()` defaults to 200, add methods default to 200, clipboard capture calls those defaults, and current search found no pin/unpin DB method or UI action. Plan the requested semantics as **10 unpinned recent items plus all pinned items**, not 10 total.
- `EqualizerControl.cs` currently renders four cyan spring bars driven by synthetic sine waves. It does not analyze music audio or use album colors. `MediaService` already receives GSMTC track metadata and thumbnail bytes; `IslandWindow.xaml.cs` loads the artwork and drives playback state. Check artwork decode sizes and current collapsed/expanded music layouts.
- The clock is currently shown in the collapsed pill when music is not playing, and hidden there while music plays. Scheduled actions/routines exist, but no obvious countdown timer/stopwatch UI or dedicated timer service was found.
- The Shell has a custom `SoulOrbControl` and `EqualizerControl`; both should detach rendering subscriptions when not active/loaded. Frozen brushes, cached/thumbnail imagery, and existing shell lifecycle should be preserved.
- `PROJECT.md` is useful context but may have stale structure or test claims. A previous Release build succeeded with zero warnings/errors, while a later test invocation stalled before test discovery/results. Verify current facts and do not present that stalled run as a passing test run.

## What to inspect

At minimum, inspect the live files and any relevant tests/docs for:

- `src/Companion.Shell/Windows/IslandWindow.xaml` and `.xaml.cs`
- `src/Companion.Shell/Windows/CircleToAskOverlay.xaml` and `.xaml.cs`
- `src/Companion.Shell/Controls/EqualizerControl.cs` and `SoulOrbControl.cs`
- `src/Companion.Shell/Resources/Theme.xaml`
- `src/Companion.Services/GlobalInputService.cs`, `MicCaptureService.cs`, `GeminiLiveService.cs`, `GeminiVisionService.cs`, `AudioPlayerService.cs`, `MediaService.cs`, and `AutonomousScheduler.cs`
- `src/Companion.Core/DatabaseService.cs`, `IDatabaseService.cs`, and `Models.cs`
- `src/Companion.Studio/MainWindow.xaml` and its theme
- Clipboard, audio, IPC, input-hook, and scenario tests; `PROJECT.md`; and `tools/verify-perf.ps1` if present.

Check the current worktree for existing user changes. Do not overwrite or discard them.

## Planning instructions

Prioritize reliability before adding more features. Build the roadmap in phases with dependencies and exit criteria. A reasonable ordering to evaluate is:

1. **Baseline and reproduce:** map Circle to Ask and live voice state transitions; define concrete repros; add safe, redacted diagnostics; identify missing prerequisites and distinguish code defects from API key, microphone, network, or device setup.
2. **Restore dependable AI voice workflows:** make normal live conversation start/stop discoverable and correctly wired; repair Circle to Ask end-to-end; separate user and assistant transcript presentation; make error, permission, cancellation, reconnect, and cleanup states explicit. Preserve Gatekeeper, untrusted-context, and tool-policy behavior.
3. **Clipboard retention and pinning:** provide pin/unpin/delete UI and DB operations; keep all pinned items and exactly the latest 10 unpinned copies; trim transactionally; handle existing records and restart persistence. Include a deliberate way to unpin/delete retained items.
4. **Activity model and Dynamic Island behavior:** plan a small Shell-level activity/state coordinator so voice, permission prompts, media, timer, clipboard feedback, and scheduled events cannot clobber each other through unrelated `SetState` calls/timers. Define priority, duration, interruption, and user-dismiss behavior. Permission prompts must remain visible and actionable. Reuse existing host, sizes, gestures, and spring engine where practical.
5. **Music Now Bar and visualizer:** use GSMTC artwork/metadata and playback events; derive a small stable palette from a thumbnail, with accessible contrast and a theme fallback. Plan a smooth album-colored One UI-inspired animation. Be explicit that GSMTC supplies metadata, not a decoded music waveform: prefer a lightweight stylized playback visualizer unless true loopback spectrum analysis is explicitly justified with its privacy and resource cost. Keep artwork decode/cache sizes bounded.
6. **Clock, timer, and activity breadth:** keep time discoverable during playback; add countdown timer (and decide whether stopwatch/alarms are in scope) that continues while the dock is collapsed, provides a clear completion activity, and does not require a perpetual high-frequency UI loop. Integrate with existing scheduler where appropriate without changing its semantics accidentally.
7. **Visual polish and performance/accessibility QA:** unify Shell/Studio design tokens, hierarchy, typography, activity-specific layouts, reduced-motion behavior, DPI/accessibility states, and responsive constraints. Measure idle and active resource usage.

Adjust this ordering if repository evidence warrants it, and explain why. Avoid estimates without evidence. Do not make a giant rewrite or change unrelated Gemini/security/service behavior.

## Required plan output

Return a concise but implementation-ready plan with:

1. A verified current-state architecture map and user-visible flows.
2. A bug diagnosis table for Circle to Ask and live speech: symptom, observed code path, plausible causes ranked by evidence, how to reproduce/confirm, and likely fix area. Mark unconfirmed causes as hypotheses.
3. Phased work items with likely files/components, dependencies, acceptance criteria, and regression risks.
4. A proposed island/activity state and priority table covering at least idle/clock, music, timer, Gemini listening/thinking/speaking, Circle to Ask, clean dictation, clipboard toast, scheduled event, and Gatekeeper prompt.
5. Clipboard retention semantics stated exactly: **keep all pinned entries plus the newest 10 unpinned entries**.
6. A focused test/manual QA matrix for API/mic unavailable, real voice input/output, interrupted speech, Circle selection across displays/DPI, empty/short selection, clipboard overflow with pins, media with/without artwork, timer while collapsed, focus preservation, pointer passthrough, and fullscreen behavior.
7. A resource/privacy guardrail section. Use measured current baseline if available; project notes previously claimed 0.0% average idle CPU and ~37 MB private working set, but validate rather than trust the claim. Avoid continuously running idle animations, unnecessary audio loopback, logging API keys/transcripts/audio, and unbounded artwork/history buffers.
8. A first implementation slice that Codex/Antigravity could safely execute after the user approves the plan, plus an explicit list of decisions that truly need user input.

Do not implement changes in this planning pass. Clearly distinguish repository facts, user reports, and hypotheses. Do not claim the issue is fixed or that tests pass without current evidence.
