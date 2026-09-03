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

- [ ] **Tests** - none written yet. Planned: `PipeProtocol` round-trip and oversized-frame
      rejection; `AgentServer` via `AgentPolicy` seam (unknown id refused, non-agent
      elevation refused, output streams back, cancel works); `IsDirectorySecured` returns
      false for inherited ACL / user-writable / user-owned directories.
- [ ] **Pin/unpin UI** - there is no way to pin a command yet, so the agent has nothing to
      run. This is the missing user-facing half of M5.
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
