# Hitodama AI — Design and Motion Handoff

Use this brief as context when reviewing the Hitodama codebase for a visual and animation makeover. The goal is a polished, distinctive Windows companion that feels calm and responsive while staying lightweight during long idle periods.

## Product summary

Hitodama AI is a voice-first Windows desktop companion. Its resident Shell lives as a compact, top-centered “island” over other apps; users expand it into a small utility dock, invoke voice and screen-selection actions with global shortcuts, and use an on-demand Studio window to configure the companion. It is a native desktop application, not a web app.

The personality is represented by a small animated Soul Orb / flame. The orb has Idle, Listening, Thinking, Speaking, Acting, and Error states, plus selectable looks such as Hitodama Flame, Cyber Orb, Solar Ember, and Void Ripple.

## Stack and architecture

- C# 12, .NET 8, WPF, targeting `net8.0-windows10.0.19041.0`.
- `Companion.Shell`: resident, single-instance overlay, tray icon, island/dock UI, global input, permission prompts, and screen-lasso overlay.
- `Companion.Studio`: separate on-demand WPF configuration app. It contains the soul/loadout editor, API-key onboarding, hotkey guide, memory browser, and security audit view.
- `Companion.Services`: spring animation solver, microphone/audio pipeline, Gemini Live/Vision, global keyboard hook, Windows controls/media, scheduler, wallpaper, and tool execution.
- `Companion.Core`: models, DPAPI-protected secrets, SQLite/WAL/FTS5, JSON-RPC contracts over the local `companion-ipc` named pipe, and Gatekeeper security interfaces.
- Shell and Studio styles are defined in `src/Companion.Shell/Resources/Theme.xaml` and `src/Companion.Studio/Resources/Theme.xaml`.

## UI surfaces and behavior

### Shell island

The Shell window is a transparent, non-activating WPF host sized 720×520 and anchored at the top center. It is designed to leave keyboard focus in the app beneath it and pass pointer hits through outside the visible island. The visible content is a single `IslandContainer` whose dimensions and corner radius morph between states; do not animate the native HWND size each frame.

Current states declared in `IslandWindow.xaml.cs`:

| State | Size | Use |
| --- | ---: | --- |
| Collapsed | 164×36 | Resting island, companion name/time/status |
| Compact live | 360×56 | Short voice/media/status activity |
| Voice active | 440×180 | Expanded voice interaction |
| Permission prompt | 440×180 | Gatekeeper approval UI |
| Expanded dock | 640×390 | Seven-tab utility surface |

The XAML is mostly consolidated in `src/Companion.Shell/Windows/IslandWindow.xaml` and its code-behind; the seven dock pages are embedded there rather than implemented as separate tab controls/files. They are Controls, Today, Stats, Clipboard, Apps, Wallpaper, and Power. The island expands/collapses via click, wheel, and shortcuts; horizontal wheel navigation switches tabs. The window auto-hides in some fullscreen situations.

### Other visual surfaces

- `CircleToAskOverlay.xaml`: fullscreen transparent selection overlay with a drawn screen-region lasso and voice question flow.
- `Companion.Studio/MainWindow.xaml`: three configuration sections — Soul, Connect & talk, and Memory & audit — styled by its own theme dictionary.
- `SoulOrbControl.cs`: custom WPF-drawn radial-gradient orb/flame; activity shapes its glow and blob silhouette.
- `EqualizerControl.cs`: four-bar music activity visual.

## Current visual direction

The existing UI is dark, compact, and glass-inspired. Its tokens include near-black graphite surfaces, translucent white borders, muted text, and cyan/indigo/emerald soul colors. Fonts use Segoe UI Variable with Segoe UI fallbacks. Cards, segmented navigation, and compact pill controls already establish a consistent base.

Some legacy coral/red accent tokens remain in the shared Shell theme (for example `AccentColor`, `AccentGlassBrush`, and `TabPillActiveBrush`), while newer soul colors use cyan/violet/emerald. Review whether the accents feel coherent and accessible; consolidate tokens instead of layering on more one-off colors. The Shell visual system should also feel related to Studio, while respecting their different surface sizes and jobs.

Design references requested by the user:

- [Refero](https://styles.refero.design/) — visual reference for polished interface patterns and composition.
- [Watermelon UI](https://ui.watermelon.sh/) — reference for refined component styling.
- [Cult UI](https://cult-ui.com/) — reference for distinctive visual details and motion.

Use these as visual inspiration. Translate relevant ideas into native WPF/XAML, keyboard-accessible controls, and the constrained island layout; do not import web/React UI wholesale or add a web runtime.

## Existing animation and resource constraints

- `Companion.Services/SpringSolver.cs` implements a second-order spring solver. `IslandAnimator` subscribes to `CompositionTarget.Rendering` while springs are moving, then unsubscribes when settled. State changes retarget existing spring values, so transitions can be interrupted.
- Island expansion animates the inner content dimensions/corner radius. Expanded content has a spring-driven opacity and a small vertical slide; content is staged around the expansion.
- Collapse uses a firmer spring than expansion; current values are defined in `IslandWindow.xaml.cs` and should be tuned only with the existing gesture/state behavior in mind.
- `SoulOrbControl` is static and unsubscribed from rendering while Idle. It redraws while in an active state; `EqualizerControl` animates while music is playing. Keep active animation scoped to visible, relevant UI, and detach rendering callbacks when inactive/unloaded.
- The Shell is intended to be resident. Idle CPU/GPU and memory use matter more than elaborate motion. Avoid always-running timers, perpetual idle loops, per-frame layout-heavy effects, large blur stacks, and unnecessary image/network dependencies.
- Existing code uses frozen gradient/solid brushes and has a 2-minute idle memory-trim path. Wallpaper/media imagery should remain cached and decoded to thumbnail dimensions.
- Keep the overlay’s hit testing, non-activation/focus behavior, tray behavior, global shortcuts, and Gatekeeper confirmation affordances intact.

## Design goals for the makeover

1. Give the Shell and Studio a coherent, memorable visual identity: restrained dark surfaces, crisp hierarchy, thoughtful spacing, and a distinctive but controlled Soul Orb accent.
2. Improve typography, density, alignment, tab selection, card hierarchy, empty/loading/error states, and small-screen usability within the existing window bounds.
3. Make motion explain state changes: clear expand/collapse choreography, quick feedback for tabs/toggles/actions, and meaningful voice-state transitions. Keep transitions interruptible and avoid motion that slows frequent use.
4. Keep the collapsed island visually quiet. Listening/speaking should be legible at a glance; Thinking, Acting, Error, and permission prompts need distinct but accessible cues.
5. Respect reduced-motion preferences if a practical WPF path exists; otherwise keep motion subtle and provide a non-animated visual state.
6. Preserve native Windows behavior and low resource use. Prefer existing WPF drawing, transforms, opacity, and the existing spring engine. Add dependencies only when there is a clear improvement that cannot be achieved with the current stack.

## Review and implementation instructions

Please inspect the current XAML, code-behind, theme dictionaries, spring/orb/equalizer controls, and relevant event handlers before proposing edits. Base changes on the actual code; `PROJECT.md` is useful context but may contain stale structure or verification claims.

Then:

1. Briefly identify the strongest visual and motion improvements and any technical risks.
2. Propose a compact token/component system and transition map before broad edits.
3. Implement the makeover in small, coherent changes. Keep existing features, data bindings/events, security prompts, keyboard workflows, tab behavior, window geometry, non-activation, and hit-test passthrough working.
4. Prefer resource-light, interruptible animation. Reuse the spring engine where appropriate; do not add a continuously running render loop to the idle Shell.
5. Avoid broad rewrites of service/business logic. Do not change Gemini behavior, IPC contracts, persistence, or Gatekeeper policy for a visual task.
6. Report changed files, design decisions, build/verification results, and any remaining visual checks that require a Windows desktop.

## Useful files

- Shell layout and interaction: `src/Companion.Shell/Windows/IslandWindow.xaml`, `src/Companion.Shell/Windows/IslandWindow.xaml.cs`
- Shell palette/control styles: `src/Companion.Shell/Resources/Theme.xaml`
- Orb and equalizer: `src/Companion.Shell/Controls/SoulOrbControl.cs`, `src/Companion.Shell/Controls/EqualizerControl.cs`
- Spring runtime: `src/Companion.Services/SpringSolver.cs`
- Lasso overlay: `src/Companion.Shell/Windows/CircleToAskOverlay.xaml`, `src/Companion.Shell/Windows/CircleToAskOverlay.xaml.cs`
- Studio UI and theme: `src/Companion.Studio/MainWindow.xaml`, `src/Companion.Studio/MainWindow.xaml.cs`, `src/Companion.Studio/Resources/Theme.xaml`
- Product/architecture notes: `PROJECT.md`

## Verification note

The latest known Release build completed with zero warnings and errors. In a later local check, `dotnet test` stalled before test discovery/results, so that run was inconclusive. `PROJECT.md` contains a claim that 239 tests pass; confirm against the current checkout rather than assuming that claim is current. Real-device visual checks should cover DPI scaling, focus preservation, pointer passthrough, fullscreen auto-hide, and the Shell’s idle resource use.
