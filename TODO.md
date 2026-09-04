# TODO

## M3 — Widget shell (done, but gaps)

- [ ] **Settings UI** — hot key, autostart, approval toggle, add/edit a command by hand.
      Everything is currently editable only by hand in `settings.json`.
- [ ] Group headers in the palette list (grouping is modelled but not rendered)
- [ ] Auto-scroll the output panel to the newest line
- [ ] Tests for `FuzzyMatcher` scoring order

## M4 — API

- [ ] Token generation with inheritance-disabled user-only ACL
- [ ] CRUD + run endpoints, SSE stream
- [ ] `CommandValidator`: agent → 403, `Origin` header → 403, non-loopback `Host` → 403,
      200-command cap, 64 KB script cap, rate limit
- [ ] Approval flow in the UI for `Approved = false` commands

## M5 — Agent (code complete; only the elevated path is unverified)

Everything is written and covered by tests, **except the elevated path itself**, which needs
a human to click a UAC dialog and has still never run:

- `Core/Ipc/PipeProtocol.cs` - length-prefixed JSON frames, per-user pipe name
- `Core/Storage/PinnedStore.cs` - read/write, pin/unpin, `SecureDirectory`, `IsDirectorySecured`
- `Core/Execution/AgentRunner.cs` - pipe client, streams output back, cancel handshake
- `Agent/AgentServer.cs` - pipe server, resolves ids against the pinned store only
- `Agent/ClientProcess.cs` - identifies the connecting exe
- `Agent/Program.cs` - `--secure-store`, `--pin`, `--unpin`
- `App/Services/AgentInstaller.cs`, `Elevated.cs`, `PinService.cs`

Outstanding:

- [x] **Tests** - 56 of them, driving the real `AgentRunner` against a real `AgentServer`
      over a real pipe in one process. `AgentPolicy` gained a `PipeName` so tests never
      collide with an installed agent. They found four bugs, now fixed and written up in
      CLAUDE.md: zero-size pipe buffers block every write; `FlushAsync` on a pipe waits for
      the peer; the agent could not read a `Cancel` during a run; the client raced its
      cancel against disposing the pipe.
- [x] **Pin/unpin UI** - right-click a command in the palette, or Ctrl+P. The confirm pane
      says what pinning means and what the command will run, then one UAC prompt runs
      `QuickJack.Agent --pin <id>`, which reads the definition from the user store itself
      and writes `pinned.json`. Unpin is the same in reverse. `Elevated` is now shared by
      the installer and `PinService`.
- [ ] `IsDirectorySecured`'s users-can-write branch is unreachable from a test: it is only
      reached for an Administrators-owned directory, and creating one needs elevation.
      Covered by the manual checklist instead.
- [ ] **Never executed elevated.** Install needs a real UAC click, so the scheduled-task
      XML, `--secure-store`, the ACL/owner changes and the whole no-prompt path are all
      unverified. Verify by hand before trusting any of it.
- [x] Whether the agent should refuse to start when its own exe is not the one recorded at
      install time: recording the path buys nothing, since an attacker who can swap the
      binary swaps it at the same path. The ACL is what matters, so both the installer and
      the agent now refuse a user-writable image outright. Written up in CLAUDE.md.


---

## Manual verification checklist

Run after M3, and again after any window-management change.

- [ ] Orb stays above a maximised browser, File Explorer, and a fullscreen video
- [ ] Orb survives unplugging a second monitor and a DPI change (does not strand offscreen)
- [ ] Hotkey summons the palette from another foreground app, and the search box takes focus
- [ ] Non-elevated command streams output live; cancel actually kills the process tree
- [ ] UAC command shows consent, output still appears inline afterward
- [ ] **Declining** the UAC dialog reports "cancelled", not an error
- [ ] `curl` registers a command → appears within a second, greyed → approving lets it run
- [ ] `curl` with `"elevation":"agent"` is refused with 403
- [ ] Agent install prompts once; a pinned command then runs with no prompt and is logged
- [ ] Agent uninstall removes the scheduled task cleanly
- [ ] Ctrl+P on a command explains what pinning means, then prompts once; the row's badge
      turns to `admin*` without a restart, and `pinned.json` names it
- [ ] Declining that prompt leaves the command unpinned and says "consent was declined"
- [ ] `%ProgramData%\QuickJack` is Administrators-owned afterwards, and a non-elevated
      `notepad` cannot save over `pinned.json`
- [ ] Unpin restores the original user-store command, elevation and all
- [ ] Pinning is refused for a command that is still awaiting approval
- [ ] Enabling the agent from a build tree is refused with "install under Program Files";
      from a Program Files install it succeeds
