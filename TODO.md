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

## M5 — Agent

- [ ] Pipe contracts; `CurrentUserOnly` + explicit `PipeSecurity`
- [ ] Client process image-path check on connect
- [ ] `pinned.json` with Administrators-write ACL; pin/unpin UI
- [ ] Scheduled task installer + uninstaller, with a plain-language warning
- [ ] Audit log

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
