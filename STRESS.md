# PalAssist 2 — Stress & Stability Checklist

Use this before shipping a build or after major stability changes.

## Guarantee

**No stuck keys** after:

- Update install / restart
- Unhandled exception (best-effort key release + log)
- Palworld exit / crash
- Normal PalAssist exit (window close or tray Exit)

**Limitation:** Task Manager “End task” cannot run managed cleanup. Defense in depth: Focus Lock, AFK safety (10 min), and **Stop all**.

## Checklist

### Long run
- [ ] Run **8+ hours** with Focus Lock **on** and Work Assist used periodically
- [ ] Confirm `PalAssist2.log` rotates and does not grow without bound
- [ ] CPU stays modest while idle AFK (game closed or unfocused)

### Focus / input
- [ ] Work Assist **on** → heavy alt-tab spam → keys release when unfocused, resume when focused
- [ ] **Stop all** while holding → F released immediately
- [ ] Exit via tray Exit and via × with “minimize to tray” **off** → no stuck F in Notepad/game

### Game lifecycle
- [ ] Kill Palworld process while Work Assist on → keys release; after 10 min AFK safety stops assists
- [ ] Restart Palworld → assists stay **off** until toggled again

### Tray / UI
- [ ] Minimize to tray → restore → toggles still work
- [ ] Rebind a hotkey → Escape cancel → game does not lose focus permanently (NOACTIVATE restored)

### Updates
- [ ] Check for updates → cancel mid-download → no leftover hung process; stage cleaned
- [ ] Install update → new process starts clean (no held keys)

### Failure injection (optional)
- [ ] After intentional fault in logs, app either continues timers or exits after releasing keys
- [ ] Open `PalAssist2.log` and confirm structured ERROR lines with state snapshot on crash paths

## Notes

- Feature tick is ~33 ms; focus poll ~100 ms with reduced process hunts while the game HWND is valid.
- All feature/window timer events marshal to the UI with non-blocking `BeginInvoke`.
