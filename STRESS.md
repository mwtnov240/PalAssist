# PalAssist 2 — Stress & Stability Checklist

Use this before shipping a build or after major stability changes.

## Guarantee

**No stuck keys** after:

- Update install / restart
- Unhandled exception (best-effort key release + log)
- Palworld exit / crash
- Normal PalAssist exit (window close or tray Exit)

**Limitation:** Task Manager “End task” cannot run managed cleanup. Defense in depth: Focus Lock, AFK safety (10 min), and **Stop all**.

**Single instance (v2.3):** Only one process per user session. A second launch signals the first to show the menu and exits.

## Checklist

### Long run
- [ ] Run **8+ hours** with Focus Lock **on** and Work/Walk Assist used periodically
- [ ] Confirm `PalAssist2.log` rotates and does not grow without bound
- [ ] CPU stays modest while idle AFK (game closed or unfocused)
- [ ] With assists **off**, log shows `Tick stopped (idle)` and no continuous 33 ms work
- [ ] Optional: heartbeat lines appear ~every 30 min while an assist stays on

### Single instance (v2.3)
- [ ] Start PalAssist → start a second copy → only **one** process in Task Manager
- [ ] Second launch shows / restores the menu on the first instance
- [ ] Hotkeys still work only once (no double toggles)

### Focus / input
- [ ] Work Assist **on** → heavy alt-tab spam → keys release when unfocused, resume when focused
- [ ] Walk Assist **on** → holds W; hotkey toggle; rebind works; Focus Lock releases W
- [ ] Work + Walk both **on** → F and W held; Stop all releases both
- [ ] **Stop all** while holding → F and W released immediately
- [ ] Exit via tray Exit and via × with “minimize to tray” **off** → no stuck F/W in Notepad/game
- [ ] Active Hold (beta) + Focus Lock: Work Assist may stay in background mode; Walk does not

### Game lifecycle
- [ ] Kill Palworld process while Work Assist on → keys release; after 10 min AFK safety stops assists
- [ ] Restart Palworld → assists stay **off** until toggled again
- [ ] After game closes, wait ~2s → overlay snaps to work area; **Insert** shows menu on-screen

### Tray / UI
- [ ] Minimize to tray → restore → toggles still work
- [ ] Rebind a hotkey → Escape cancel → game does not lose focus permanently (NOACTIVATE restored)
- [ ] Menu remains visible after game window resize / multi-monitor change (clamp)

### Updates
- [ ] Check for updates → cancel mid-download → no leftover hung process; stage cleaned
- [ ] Install update → new process starts clean (no held keys)

### Failure injection (optional)
- [ ] After intentional fault in logs, app either continues timers or exits after releasing keys
- [ ] Open `PalAssist2.log` and confirm structured ERROR lines with state snapshot on crash paths

## Notes

- Feature tick is ~33 ms **only while an assist is enabled** (v2.3 adaptive).
- WindowTracker poll ~100 ms when busy, ~400 ms when idle (focus still via WinEvent).
- All feature/window timer events marshal to the UI with non-blocking `BeginInvoke` where required.
- Active Hold remains beta: background delivery uses window-targeted messages (game-dependent).
