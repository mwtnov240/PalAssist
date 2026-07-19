# PalAssist Changelog

## 2.0.0

**PalAssist 2 — stability & long-run hardening**

- Rebranded as **PalAssist 2** (exe remains `PalAssist.exe` for seamless auto-update)
- Flattened repository layout; professional README + `STRESS.md`
- Application icon (Pal Sphere–inspired original mark)
- Thread-safe feature state (lock around enable / suspend / release / tick)
- Feature tick ~33 ms with full try/catch (no single failure kills the loop)
- WindowTracker: safer events, less process spam when game is already found
- UI marshalling uses `BeginInvoke` (avoids timer/UI deadlocks)
- Hotkey rebind always restores `WS_EX_NOACTIVATE` (try/finally)
- Rotating size-capped log `PalAssist2.log` with error budget
- UpdateService: cancel on dispose, stage cleanup, more resilient apply script
- Stronger `ForceReleaseCommonKeys` (double KeyUp)
- Removed unused `ProfileWorkFeature`
- **Guarantee:** no stuck keys after update install, unhandled exception (best-effort), game exit, or PalAssist exit

## 1.5.0

- **Vertical tabs** — left nav rail for more content space
- **Help hints (?)** on features — hover for what / when to use
- **Emergency Stop all** — disable assists and release held keys
- **AFK safety** — auto-stop assists if Palworld is closed 10+ minutes (on by default)
- **Config Manager** — open folder, export, import, reset to defaults
- Safer key release on exit and unhandled exceptions (`PalAssist-crash.log`)

## 1.4.0

- **What's Changed** tab — full version history in-app (newest first, scrollable)
- Beta cleaned up: only **Smart Work Assist** remains (Work Profiles and Setup Wizard removed)
- Removed **AI Assists** placeholder tab

## 1.3.0

- **Smart Work Assist** (Beta): when enabled, Work Assist taps F once, waits 1s, then holds F so items on workstations are picked up first
- Removed **Session Timer** from Beta

## 1.2.0

- **Focus Lock** graduated from Beta — works without enabling Experimental features
- Faster, more accurate focus detection (WinEvent hook + process-aware matching + minimize handling)
- System tray icon with Show Menu / Hide Menu / Exit
- Minimize to tray (optional) when closing from the taskbar
- Sound feedback for assist toggles (optional focus / update sounds)
- Appearance: menu opacity, UI scale, and color themes (Cyan, Purple, Green, Amber, Red)
- More crosshair colors (Magenta, Orange, Blue, Pink, Black, Lime)
- In-app changelog and What's New popup after updates

## 1.1.0

- Work Assist (hold F) and crosshair overlay
- Beta: Focus Lock, Work Profiles, Session Timer, Setup Wizard
- Auto-update from GitHub Releases
- Config persistence and HUD indicators

## 1.0.6

- Save Settings works again
- Fixed config save failing silently when HUD/menu positions were unset (NaN broke JSON serialization)
- More reliable config.json writes (temp file + replace)

## 1.0.5

- Header version matches the real build
- Appears on the Windows taskbar with live version
- Window title shows the live version

## 1.0.4

- Save Settings button disabled temporarily (settings still auto-save)
- Auto-checks for updates on boot with Yes/No before install
- Manual Check for Updates uses the same prompt

## 1.0.3

- Work Assist holds F for work/interactions
- More reliable keyboard input for Palworld

## 1.0.2

- Early release packaging and update pipeline improvements

## 1.0.1

- Initial public builds and bug fixes
