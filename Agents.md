# AGENTS.md — Repository Guardrails for Void AI
- **Build:** `dotnet build Companion.sln -c Release` (Target: `net8.0-windows10.0.19041.0`)
- **Test:** `dotnet test Companion.sln --blame-hang-timeout 15s`
- **Perf Gate:** `powershell -ExecutionPolicy Bypass -File tools/verify-perf.ps1` (Must stay at 0.0% idle CPU and < 85 MB Private Working Set)
- **Window Invariant:** Never resize the 720x520 `IslandWindow` HWND per frame; animate only `IslandContainer`. Keep `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` and `WM_NCHITTEST` (`HTTRANSPARENT = -1`) intact.
- **Clipboard Invariant:** Retain ALL pinned items (`is_pinned = 1`) + at most the newest 10 unpinned items (`is_pinned = 0`).