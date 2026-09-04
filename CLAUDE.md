# QuickJack Widget

A floating, always-on-top command launcher for Windows. A small orb sits over every window;
clicking it (or a global hotkey) expands a searchable palette of saved PowerShell / cmd
commands. Commands can run elevated. A loopback HTTP API lets any script register a new
command, which appears on the widget immediately - see [API.md](API.md) for using it.

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
| `src/QuickJack.Core` | `net10.0-windows` | models, stores, execution, IPC contracts — no UI, fully testable |
| `src/QuickJack.Api` | `net10.0-windows` | Kestrel endpoints (`FrameworkReference` on `Microsoft.AspNetCore.App`) |
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
directly. The UAC path therefore elevates a wrapper script that starts the real script as
its own child WITH redirection - to files - which the non-elevated widget tails.
Worth knowing before touching `UacProcessRunner`.

**Parameters bind to environment variables (`QJ_<NAME>`) by default.** Nothing is spliced
into script text. Inline `{{name}}` substitution is supported because people reach for it,
but only for parameters declaring a validating `Pattern`, and the value must additionally be
free of control characters — a permissive pattern like `[\s\S]*` would otherwise let a
newline smuggle in a second command. `ParameterBinder` is the single most security-relevant
piece of logic in the app; treat its tests as load-bearing.

**In cmd, `%QJ_NAME%` is not safe and is rejected.** cmd expands `%VAR%` *before* parsing the
line, so a value containing `&` or `|` runs as a second command — the environment is not
inert in cmd the way `$env:X` is in PowerShell. The cmd preamble therefore enables delayed
expansion, scripts must read parameters as `!QJ_NAME!` (which expands after parsing), and
`ParameterBinder` refuses to bind a cmd script using the `%`-form. Found by a test that
expected the value to be echoed verbatim and got it executed instead.

**The elevated wrapper never has values interpolated into its source.** Parameter values go
into a JSON file the wrapper reads; paths that do reach its source (the interpreter,
`WorkingDirectory` — which the API can set) go through `Ps.Quote`, where doubling the single
quote is a complete escape. Generating PowerShell around user text would reintroduce exactly
the injection problem `ParameterBinder` exists to prevent.

**`Start-Process -PassThru` loses the exit code.** PowerShell releases the process handle, so
`$child.ExitCode` reads back as 0 whatever the process returned. The wrapper touches
`$child.Handle` to cache it. Do not remove that line; two tests depend on it.

**`InvariantGlobalization` must stay off.** WPF data binding calls
`XmlLanguage.GetSpecificCulture()` and throws `Cannot find non-neutral culture related to
'en-us'` without real culture data — the window never renders and, because `OnStartup` is
`async void`, nothing reports it. It was in `Directory.Build.props` as a reflex; it is gone.

**No DWM backdrop on the widget window.** `DWMWA_SYSTEMBACKDROP_TYPE` paints the whole window
rectangle, which fights `AllowsTransparency` and turns the round orb into a grey square.
The window paints its own dark surface instead.

**Positioning uses physical pixels via `SetWindowPos`, not `Window.Left`/`Top`.** WPF's
device-independent units get slippery across monitors with different scaling; screen bounds
come from WinForms `Screen` in the same physical units, so no conversion is involved.

**`UseWindowsForms` alongside `UseWPF` collides on common type names.** `Application`,
`MouseEventArgs`, `KeyEventArgs`, `ButtonBase`, `TextBoxBase` and `Clipboard` exist in both.
`GlobalUsings.cs` aliases each to the WPF one; the tray icon and `Screen` name the WinForms
type explicitly.

**Startup is logged to `%APPDATA%\QuickJack\app.log`.** A tray app has no console and no
window to print to, so a startup failure is otherwise completely silent. `OnStartup` is
`async void`, so its body is wrapped in a try/catch that logs and shows a message rather
than failing invisibly — this is how the globalization bug above was found in one run.

**The hot key falls back rather than failing.** Ctrl+Alt+Space is frequently taken (IME and
emoji pickers claim it — it was taken on the dev machine). `HotKeyService.Attach` walks a
short fallback list and reports which combination it actually got, because a hot key that
silently does nothing, with no settings UI yet to change it, is a dead end.

**Cancelling an elevated run goes through a sentinel file, not `Process.Kill`.** A
medium-integrity process cannot kill a high-integrity one, so the widget writes a `cancel`
file and the elevated wrapper — which *can* kill its own child — does it, with `taskkill /T`
so the whole tree goes.

**`UacProcessRunner` has an `elevate: false` test seam.** It launches the wrapper without the
runas verb, so wrapper generation, output tailing, cancellation and exit-code recovery are
all covered by automated tests; only the ShellExecute verb differs. The genuinely-elevated
and UAC-declined cases stay on the manual checklist.

**A named pipe created with the default buffer sizes of zero blocks every write until the
peer reads it.** Not "buffers as needed" — with no buffer, `WriteFile` completes only when
the reader takes the bytes, so writing to a peer that has stopped reading hangs forever with
no error. The agent's rejection path does exactly that (it writes "not permitted" and stops
reading), which hung the client's *previous* write, which hung the test host. The server end
therefore passes `PipeProtocol.BufferBytes` for both directions. Cost of finding this: an
afternoon; it presents as a silent hang, and the test runner reports "test host crashed".

**Never `FlushAsync` a pipe.** On a pipe that is `FlushFileBuffers`, which waits for the peer
to drain everything written — a second way to hang on a peer that is not reading. The write
has already handed the bytes over; there is nothing to flush. One `WriteAsync` per frame
also means a frame can never be interleaved with another writer's and left half-written.

**The agent keeps reading while a run is in progress.** Awaiting the run inside the read loop
meant a `Cancel` was only read once the run it was meant to stop had already finished — so
cancellation could not work at all. Runs are now tasks owned by their connection, and frames
are serialised through a per-connection write gate. Connections are likewise served on their
own tasks, so a second command does not look like "the agent is not running" until the first
finishes. When a connection drops, its runs are cancelled: nothing is left to receive their
output, and an elevated process nobody can see or stop is what this design must not leave.

**Pinning goes through the same elevated helper as installation, and takes an id.**
`QuickJack.Agent --pin <id>` runs elevated via ShellExecute `runas` — one UAC prompt per pin,
which is the cost that makes a no-prompt button something the user granted rather than
something a script arranged. It is handed an id and reads the definition out of the user's
own store itself, for the same reason the running agent does: a caller that could supply the
script body could pin something other than what the user was shown before the prompt. Pin
copies rather than moves, so the user-store original survives and unpinning is a complete
undo. `PinnedStore.Pin` refuses an unapproved command — promoting something the user has
never reviewed straight to silent administrator is what the approval gate exists to stop.

**Cancelling waits for the agent to confirm.** The client used to fire the cancel message
from a token registration and immediately dispose the pipe, racing the write against the
teardown — so the elevated process could survive a cancel with nothing left able to stop it.
It now sends the cancel and keeps reading for a short grace period, and says plainly that
the command may still be running if the agent never answers.

---

## Status

- [x] **M1 Skeleton** — solution, projects, `CommandDef`, `CommandStore`.
- [x] **M2 Execution** — runners, parameter binding, streaming output. 96 tests.
- [x] **M3 Widget shell** — orb, drag, edge snap, palette, tray, hotkey. Verified end to end.
- [x] **M4 API** — Kestrel, token auth, CRUD + run, SSE, guardrails, approval flow. 27 tests.
      Documented in `API.md`.
- [~] **M5 Agent** — IN PROGRESS. Pipe protocol, pinned store, agent server, AgentRunner and
      the scheduled-task installer are written. 42 tests now drive the real client against a
      real agent over a real pipe, in-process; they found four bugs, all listed above. Pin
      and unpin are wired up end to end: right-click a command or press Ctrl+P, confirm what
      it means, and one UAC prompt writes it to the pinned store. 145 tests.
      Still outstanding: **the genuinely elevated path has never been executed** — installing
      the agent, and pinning, both need a real UAC click. See TODO.md.

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
