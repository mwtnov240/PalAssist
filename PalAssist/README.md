# PalAssist V1.0

A modern, external overlay & input-assist tool for **Palworld** on Windows.

> **Important:** PalAssist does **not** read game memory, inject DLLs, or modify network packets. It uses only standard Windows input simulation (`SendInput` with scan-codes) and a transparent overlay window.

---

## Features

| Feature | Description | Default Hotkey |
|---|---|---|
| **Hold E Assist** | Continuously holds the E key for interactions | `F1` |
| **Sprint Assist** | Auto-walks forward (W) with timed sprint/recovery cycles (Shift) | `F2` |
| **Overlay Menu** | Tabbed dark-themed menu on top of the game | `Insert` |
| **HUD Indicator** | Consolidated status panel showing active assists | Configurable |
| **Config Persistence** | All settings saved to `config.json` | — |

### Sprint Assist Logic
When enabled, Sprint Assist:
1. Immediately holds **W** (forward movement).
2. Holds **Shift** for the configured **Sprint Duration** (default 8s).
3. Releases **Shift** for the **Recovery Duration** (default 4s) so stamina regenerates.
4. Repeats the sprint/recovery cycle automatically.
5. If **Pause when dodging** is on and **Ctrl** is pressed, all keys release for 1.0 second, then resume.

---

## Requirements

- **Windows 10/11** (x64)
- **.NET 8.0+ SDK** — [Download here](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## How to Build

```powershell
cd PalAssist
dotnet build -c Release
```

Output: `PalAssist\bin\Release\net8.0-windows\PalAssist.exe`

---

## How to Run

1. **Launch Palworld** (or any window for testing).
2. **Run `PalAssist.exe`**.
3. Press **Insert** to show/hide the overlay menu.
4. Use the **Assists** tab to toggle features, adjust sprint timings, etc.
5. Use the **Settings** tab to rebind hotkeys and customise the HUD position.

---

## Menu Tabs

| Tab | Contents |
|---|---|
| **Assists** | Hold E toggle, Sprint Assist toggle + sliders + dodge pause option |
| **AI Assists** | Placeholder — "Coming in a future update" |
| **Settings** | Hotkey rebinding, HUD position preset/drag, About + update check |

---

## Project Structure

```
PalAssist/
├── PalAssist.csproj
├── App.xaml / App.xaml.cs         # Global styles (toggle, tabs, slider, rebind button)
├── MainWindow.xaml                # Tabbed overlay UI
├── MainWindow.xaml.cs             # All overlay logic
│
├── Win32/
│   └── NativeMethods.cs           # P/Invoke (window, input, hotkeys, GetAsyncKeyState)
│
├── Core/
│   ├── InputSimulator.cs          # Scan-code key simulation
│   ├── HotkeyManager.cs           # Global hotkey registration
│   ├── WindowTracker.cs           # Palworld window detection & tracking
│   └── ConfigManager.cs           # JSON config + KeyHelper utility
│
└── Features/
    ├── IFeature.cs                # Feature interface
    ├── FeatureManager.cs          # Feature registry + 60Hz tick loop
    ├── HoldEFeature.cs            # Hold E implementation
    └── SprintAssistFeature.cs     # Sprint Assist state machine
```

---

## How to Add a New Feature

1. **Create a class** in `Features/` that implements `IFeature`:
   ```csharp
   public class MyFeature : IFeature
   {
       public string Name => "My Feature";
       public string Description => "Does something cool.";
       public bool IsEnabled { get; private set; }
       public void OnEnable()  { IsEnabled = true;  /* start action */ }
       public void OnDisable() { IsEnabled = false; /* stop action */  }
       public void Update()    { /* called ~60 Hz while enabled */ }
   }
   ```

2. **Register** in `MainWindow.xaml.cs` → `OnLoaded()`:
   ```csharp
   var myFeature = new MyFeature();
   _featureManager.Register(myFeature);
   ```

3. **Add a UI toggle** in `MainWindow.xaml` (copy the Hold E card block and update names).

4. **(Optional)** Add a hotkey in `AppConfig` and register it in `RegisterConfigHotkeys()`.

---

## Configuration

All settings persist to `config.json`:
```json
{
  "holdE_enabled": false,
  "sprint_enabled": false,
  "sprint_duration": 8.0,
  "sprint_recovery": 4.0,
  "sprint_pauseDodge": false,
  "hotkey_menu": "Insert",
  "hotkey_holdE": "F1",
  "hotkey_sprint": "F2",
  "hud_preset": "TopRight",
  "hud_draggable": false
}
```

---

## License

This project is provided as-is for personal use.
