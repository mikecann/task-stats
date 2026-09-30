# Agent guidance for task-stats

This repo contains one Windows taskbar overlay, built with C# and .NET 10 WinForms.
All source lives here. `install.ps1` wires launchers to this clone.

## Rules

- Never put source in `C:\dev\tools`. It contains generated command stubs and shortcuts only. Large external binaries belong there, not in git; do not commit `.exe` or `.dll` files.
- Use test-first development for non-trivial changes. Write or update the test first. Extract a test seam first if needed.
- When behaviour, UI copy, layout, persistence, startup or a tested contract changes, update affected tests and rerun them after implementing the change.
- Test before committing, then run the actual tool on Windows. Run PowerShell scripts directly and silent launchers via `wscript.exe`, and check exit codes.
- GUI/taskbar launches must not flash a console. Keep the `task-stats.vbs` silent-launch pattern and use it in shortcuts.
- Batch files and generated stubs must be ASCII. Write them with `-Encoding ASCII` and avoid curly quotes or non-ASCII punctuation.
- Editing existing files needs no reinstall because stubs point at the live clone. After C# changes, rebuild. Rerun `install.ps1` when installer wiring changes or the clone moves.
- `install.ps1` runs this repo's `deps.ps1`; `-SkipDeps` skips dependency checks. Dependency scripts must be idempotent, self-contained, and give clear coloured output. Check system commands with `Get-Command`. For large manual-download binaries, check and explain rather than downloading automatically.
- Keep the default compiled output and settings under `%LOCALAPPDATA%\task-stats`. No settings migration is needed because the tool name is unchanged.
- The optional OpenRouter judge reads `.env` in this clone root. Never commit secrets or make paid AI calls as part of normal CI.
- Installer changes must preserve other tools in the shared `C:\dev\tools` directory and shared PATH entry. This tool has no Explorer context-menu verbs.
- PR descriptions start with `## Why`, giving the reason for the change in plain language. Avoid em dashes and UI eyebrows/kickers.

## task-stats specifics

Replacement for TrafficMonitor / XMeters. Displays NET↑/↓, CPU, GPU, MEM as
sparkline graphs on the right side of the Windows taskbar, positioned just to
the LEFT of the system clock (detected via `TrayNotifyWnd`).

### Icons

Right-click menu icons come from the **famfamfam silk icon set** (Mark James, CC BY 2.5).
Source: https://www.famfamfam.com/lab/icons/silk/
The PNGs live in `icons\` and are embedded into the assembly as manifest resources via
`<EmbeddedResource Include="icons\*.png" />` in `task-stats.csproj`.

### Architecture

- **Requires .NET 10 SDK for builds.** Build with `dotnet build`; runtime host is `net10.0-windows`.
- Project file: `task-stats.csproj` (SDK-style, targets `net10.0-windows`, embeds `icons\*.png` as manifest resources).
- Compiled output is cached at `%LOCALAPPDATA%\task-stats\task-stats.exe`.
- `task-stats.vbs` is the primary silent launcher and starts the built EXE directly.
- `task-stats.ps1` is only a compatibility wrapper around the EXE, not the primary host.

### Dev workflow

```
cd task-stats
.\build-and-run.bat    # kill old instance + compile + launch
```
After code changes to any `.cs` file, just re-run `build-and-run.bat`.

### Test workflow

```
cd task-stats
.\run-unit-tests.bat
.\run-integration-tests.bat
.\run-tests.bat
.\run-e2e-tests.bat
```

- `run-unit-tests.bat` covers pure logic like layout math, formatting, buffers,
  and settings round-trips.
- `run-integration-tests.bat` covers Windows-backed behaviour like settings
  persistence, startup registration, and live metric sampling contracts.
- `run-tests.bat` runs everything except the AI screenshot judge.
- `run-e2e-tests.bat` captures deterministic overlay screenshots and then uses
  OpenRouter vision to check them. This is opt-in because it costs money and
  depends on external services.
- For `task-stats`, prefer deterministic fake-data tests before relying on
  manual tray screenshots.
- If you change rendering or layout, run the deterministic visual harness with `-SkipAI` before committing; run the paid judge only when explicitly requested.
- If you change behaviour covered by tests, rerun the affected test command
  after the implementation change, not just before it.

### Key implementation details

- `OverlayForm` is a frameless `WS_POPUP` + `HWND_TOPMOST` WinForms Form.
- `TransparencyKey = BackColor` makes the dark background see-through so the
  taskbar shows through. Only sparklines and text are visible.
- Position: `TrayLeftEdge()` finds `Shell_TrayWnd → TrayNotifyWnd` via
  `FindWindowEx` + `GetWindowRect` to know where the clock starts.
- `StartPosition = Manual` is critical - without it, `Show()` overrides the
  position set in the constructor.
- Z-order: a 100 ms timer re-asserts `HWND_TOPMOST` + a `WM_WINDOWPOSCHANGED`
  handler does it immediately on any z-order change.
- GPU: NVML P/Invoke (`nvml.dll`) - no `nvidia-smi` subprocess.
- CPU: `PerformanceCounter("Processor", "% Processor Time")` - aggregate +
  per-core for the XMeters-style grid mode.
- Settings: `%LOCALAPPDATA%\task-stats\settings.json` (richly commented JSON).
  Right-click overlay → Settings to change via UI.

### Known limitations

- In exclusive-fullscreen mode (rare - most modern games use borderless) the
 overlay may briefly disappear and return within ~100 ms.

### Important paths for task-stats

| Path | What it is |
|---|---|
| `task-stats.csproj` | SDK-style .NET project file |
| `src\Native.cs` | Win32 P/Invoke + NVML declarations |
| `src\Settings.cs` | JSON-backed settings |
| `src\Metrics.cs` | CircularBuffer + PerformanceCounter/NVML sampling |
| `src\OverlayForm.cs` | Layered window rendering + hit-test + menu |
| `src\SettingsForm.cs` | Tabbed settings dialog |
| `src\App.cs` | DarkRenderer + App entry point |
| `src\Program.cs` | EXE entry point and single-instance guard |
| `icons\` | famfamfam silk icons - CC BY 2.5, https://www.famfamfam.com/lab/icons/silk/ |
| `build.bat` | Builds via `dotnet build` |
| `build-and-run.bat` | Full dev cycle: kill + build + launch |
| `kill.bat` | Kills `task-stats.exe` and legacy PowerShell-hosted instances |
| `%LOCALAPPDATA%\task-stats\task-stats.exe` | Compiled output (not in git) |
| `%LOCALAPPDATA%\task-stats\settings.json` | User settings (not in git) |
| `C:\Windows\System32\nvml.dll` | NVIDIA GPU monitoring (ships with drivers) |
