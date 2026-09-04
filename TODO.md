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

## M5 — Agent (IN PROGRESS - resume here)

Written and compiling, but **nothing in M5 has been run or tested yet**:

- `Core/Ipc/PipeProtocol.cs` - length-prefixed JSON frames, per-user pipe name
- `Core/Storage/PinnedStore.cs` - read/write, `SecureDirectory`, `IsDirectorySecured`
- `Core/Execution/AgentRunner.cs` - pipe client, streams output back, cancel message
- `Agent/AgentServer.cs` - pipe server, resolves ids against the pinned store only
- `Agent/ClientProcess.cs` - identifies the connecting exe
- `App/Services/AgentInstaller.cs` - scheduled-task XML, install/uninstall

Outstanding:

- [x] **Tests** - 42 of them, driving the real `AgentRunner` against a real `AgentServer`
      over a real pipe in one process. `AgentPolicy` gained a `PipeName` so tests never
      collide with an installed agent. They found four bugs, now fixed and written up in
      CLAUDE.md: zero-size pipe buffers block every write; `FlushAsync` on a pipe waits for
      the peer; the agent could not read a `Cancel` during a run; the client raced its
      cancel against disposing the pipe.
- [ ] **Pin/unpin UI** - there is no way to pin a command yet, so the agent has nothing to
      run. This is the missing user-facing half of M5. Needs: a "pin" action in the palette
      or settings that elevates once to write `pinned.json`, and an "unpin" that does the
      reverse. `PinnedStore.Write` already requires administrator; the UI has to shell out
      to an elevated helper the way `AgentInstaller` does.
- [ ] `IsDirectorySecured`'s users-can-write branch is unreachable from a test: it is only
      reached for an Administrators-owned directory, and creating one needs elevation.
      Covered by the manual checklist instead.
- [ ] **Never executed elevated.** Install needs a real UAC click, so the scheduled-task
      XML, `--secure-store`, the ACL/owner changes and the whole no-prompt path are all
      unverified. Verify by hand before trusting any of it.
- [ ] Decide whether the agent should refuse to start when its own exe is not the one
      recorded at install time.


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
