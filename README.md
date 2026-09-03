# QuickJack

A floating, always-on-top command launcher for Windows.

A small orb sits over every window. Click it — or press a global hotkey — and it expands into
a searchable palette of your saved PowerShell and cmd commands. Pick one, it runs, and the
output streams back inline. Commands can run elevated.

A loopback HTTP API lets any script register a new command, which shows up on the palette a
second later.

## Status

Working and usable: the widget, command execution (normal and elevated), and the registration
API. The no-prompt elevated agent is written but **not yet tested** — see
[TODO.md](TODO.md).

| Milestone | State |
|---|---|
| Command model and store | done |
| Execution — local, UAC-elevated, interactive | done, 96 tests |
| The widget — orb, palette, tray, hotkey | done, verified on screen |
| Registration API | done, 27 tests, see [API.md](API.md) |
| No-prompt elevated agent | **in progress, untested** |

## Running it

Needs the .NET 10 SDK on Windows.

```bash
dotnet run --project src/QuickJack.App
```

The orb appears bottom-right on first run. Drag it anywhere; it snaps to the nearest screen
edge. `Ctrl+Alt+Space` opens the palette from anywhere (it falls back to another combination
if that one is taken, and tells you which).

```bash
dotnet test
```

## Registering a command

```bash
QJ_TOKEN=$(cat "$APPDATA/QuickJack/api-token")

curl -X POST http://127.0.0.1:47821/api/commands \
  -H "Authorization: Bearer $QJ_TOKEN" \
  -H "X-QuickJack-Client: my-scripts" \
  -H "Content-Type: application/json" \
  -d '{"name":"Flush DNS","shell":"cmd","script":"ipconfig /flushdns","elevation":"uac"}'
```

It appears on the palette immediately, greyed with a `new` badge until you approve it once.
Full reference in [API.md](API.md).

## A note on what this is

A tool that runs admin commands *and* lets other processes add commands to it is, built
naively, a local privilege-escalation machine. This one is built around an explicit trust
boundary instead:

| Elevation | Who can create it | Prompt when it runs |
|---|---|---|
| `none` | UI or API | none |
| `uac` | UI or API | Windows consent, every time |
| `agent` | **UI only, and creating one costs a UAC prompt** | none |

The API is refused — with 403, unconditionally — if it asks for `agent`. API-registered
commands also arrive unapproved and cannot run until you approve them once, so a rogue script
cannot silently plant a button you fat-finger.

[CLAUDE.md](CLAUDE.md) explains the reasoning, the invariants that must not be weakened, and
the Windows-specific traps that shaped the implementation.

## Layout

```
src/QuickJack.Core     models, stores, execution, IPC   (no UI, fully testable)
src/QuickJack.Api      Kestrel endpoints on 127.0.0.1
src/QuickJack.App      the WPF widget
src/QuickJack.Agent    the elevated agent
```

Dependencies are deliberately near-zero: `CommunityToolkit.Mvvm` and xUnit.
