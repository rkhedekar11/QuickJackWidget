# QuickJack Widget

A floating, always-on-top command launcher for Windows. A small orb sits over every window;
clicking it (or a global hotkey) expands a searchable palette of saved PowerShell / cmd
commands. Commands can run elevated. A loopback HTTP API lets any script register a new
command, which appears on the widget immediately.

**Target:** .NET 10, WPF. Build and run with:

```bash
dotnet run --project src/QuickJack.App
```

---

## The trust boundary — read this first

This tool is, by construction, a thing that runs admin commands *and* lets other processes
add commands to it. Built naively that is a local privilege-escalation machine: a rogue
script POSTs a command marked "admin" and waits for the user to click it.

The whole design turns on one table:

| Elevation | Who can create it | Prompt on run | Stored in |
|---|---|---|---|
| `None` | UI **or** API | none | user store |
| `Uac` | UI **or** API | Windows UAC consent, every run | user store |
| `Agent` | **UI only, and creating one costs a UAC prompt** | none | pinned store |

**Invariants that must never be weakened:**

1. The HTTP API rejects `elevation: "agent"` with 403. No flag, no config, no override.
2. `CommandStore.UpsertAsync` throws on `ElevationMode.Agent` — defence in depth behind (1),
   so even a bug in the API layer cannot write one.
3. Agent-mode commands live only in `%ProgramData%\QuickJack\pinned.json`, ACL'd
   Administrators-write / Users-read. Planting a no-prompt-admin command therefore itself
   costs a UAC prompt. **This is the property that makes the Agent defensible.** Without it,
   the Agent is a free escalation for anything running as the user.
4. The Agent accepts a command *id*, never script text, and re-reads `pinned.json` itself to
   resolve it. A compromised widget process cannot turn the Agent into an arbitrary shell.
5. API-registered commands land `Approved = false` and cannot run until approved once in the
   UI, so a rogue caller cannot silently plant a button the user fat-fingers.

**Why the Agent runs as the interactive user with an elevated token, not as SYSTEM:** it can
then only do what the user could already do by clicking through UAC. That makes it a UAC
bypass for the user's own account (UAC is explicitly not a security boundary per Microsoft),
not a SYSTEM escalation. It also means the user profile, PATH, and per-user tooling (Docker,
npm, nvm) work without special handling.

---

## Architecture

```
┌─ QuickJack.App ──────────────────────────────┐   medium IL, current user, autostarts
│  WPF orb + palette + settings                │
│  hosts ──► QuickJack.Api (Kestrel 127.0.0.1) │◄── curl / scripts / CI  (Bearer token)
│  │                                           │
│  ├─ None  → spawn powershell.exe             │  no prompt
│  ├─ Uac   → ShellExecute verb=runas          │  UAC consent each run
│  └─ Agent → named pipe ──────────┐           │  no prompt (opt-in)
└──────────────────────────────────┼───────────┘
                                   ▼
┌─ QuickJack.Agent ────────────────────────────┐   HIGH IL, same user, Scheduled Task
│  pipe server, ACL = current user SID only    │   "Run with highest privileges"
│  runs ONLY ids present in pinned.json        │   installed once, via one UAC prompt
└──────────────────────────────────────────────┘
```

### Projects

| Project | TFM | Role |
|---|---|---|
| `src/QuickJack.Core` | `net10.0` | models, stores, execution, IPC contracts — no UI, fully testable |
| `src/QuickJack.Api` | `net10.0` | Kestrel endpoints (`FrameworkReference` on `Microsoft.AspNetCore.App`) |
| `src/QuickJack.App` | `net10.0-windows` | WPF widget; `UseWPF` + `UseWindowsForms` (tray icon) |
| `src/QuickJack.Agent` | `net10.0-windows` | elevated pipe server |

Dependencies are deliberately near-zero: `CommunityToolkit.Mvvm` in the app, xUnit in tests.
The tray icon uses the in-box WinForms `NotifyIcon` rather than a third-party package.

### State on disk

| File | ACL | Written by | Max elevation |
|---|---|---|---|
| `%APPDATA%\QuickJack\commands.json` | user | UI + API | `Uac` |
| `%ProgramData%\QuickJack\pinned.json` | **Admin write, user read** | UI, via one UAC prompt | `Agent` |
| `%APPDATA%\QuickJack\settings.json` | user | UI | — |
| `%APPDATA%\QuickJack\api-token` | user only, inheritance disabled | app, first run | — |
| `%ProgramData%\QuickJack\agent.log` | admin write | Agent | — |

`QuickJackPaths.Under(root)` redirects all of it under one directory, which is how tests get
isolation without touching the real profile.

---

## Decisions and why

**WPF, not WinUI 3 / Electron.** The .NET 10 SDK is the only toolchain on the dev machine
(no Node, no Python), and WPF has by far the best story for a transparent, borderless,
topmost window with Win32 interop — which is most of what this app is.

**`CommandStore` is the single writer.** Every write rewrites the whole file, so a lost update
silently drops commands. Writes are serialised through a `SemaphoreSlim` and land via
temp-file + `File.Replace`, so a crash or a concurrent reader never sees a half-written store.
Reads are lock-free against an immutable snapshot swapped with `Volatile.Write`.

**Pinned wins on an id collision.** It is the store a non-admin process cannot forge, so on
conflict it must be the one that takes effect — otherwise a user-store entry could shadow a
trusted pinned command.

**Scripts are written to a temp file, never passed via `-Command`.** This sidesteps quoting
and multi-line problems entirely and keeps user script text out of any command line.

**`Verb = "runas"` requires `UseShellExecute = true`, which forbids stream redirection.**
These are mutually exclusive in Win32, so an elevated child's stdout *cannot* be captured
directly. The UAC path therefore writes to a transcript file that the non-elevated widget
tails. Worth knowing before touching `UacProcessRunner`.

**Parameters bind to environment variables (`QJ_<NAME>`) by default.** Nothing is spliced
into script text. Inline `{{name}}` substitution is supported because people reach for it,
but only for parameters declaring a validating `Pattern`. `ParameterBinder` is the single
most security-relevant piece of logic in the app; treat its tests as load-bearing.

---

## Status

- [x] **M1 Skeleton** — solution, projects, `CommandDef`, `CommandStore`, 29 tests.
- [ ] **M2 Execution** — runners, parameter binding, streaming output.
- [ ] **M3 Widget shell** — orb, drag, edge snap, palette, tray, hotkey, autostart.
- [ ] **M4 API** — Kestrel, token auth, CRUD + run, guardrails, approval flow.
- [ ] **M5 Agent** — pipe, pinned store, scheduled task installer, audit log.

M2 and M3 were swapped relative to the original plan: building execution first means the
widget binds to real commands and real output on day one, instead of a hardcoded list that
gets thrown away.

M1–M4 stand alone as a complete tool. M5 is pure convenience and can be deferred
indefinitely without stranding anything.

---

## Open questions

- Should the palette accept a free-typed one-off command, or only saved ones? Structurally
  free, but it means the fastest path to running something is no longer reviewed — so it
  should probably never be allowed to reach the Agent.
- Per-command output history retention, and whether it belongs on disk at all given commands
  can print secrets.
- Should `pwsh` (PowerShell 7) be auto-detected as the default shell when present?
