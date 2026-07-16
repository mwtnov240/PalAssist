# PalAssist V1.0

A modern, external overlay & input-assist tool for **Palworld** on Windows.

> **Important:** PalAssist does **not** read game memory, inject DLLs, or modify network packets. It uses only standard Windows input simulation (`SendInput` with scan-codes) and a transparent overlay window.

---

## Features

| Feature | Description | Default Hotkey |
|---|---|---|
| **Work Assist** | Continuously holds the F key for work/interactions | `F1` |
| **Sprint Assist** | Auto-walks forward (W) with timed sprint/recovery cycles (Shift) | `F2` |
| **Overlay Menu** | Tabbed dark-themed menu on top of the game | `Insert` |
| **HUD Indicator** | Consolidated status panel showing active assists | Configurable |
| **Config Persistence** | All settings saved to `config.json` | — |
| **Auto Update** | Checks GitHub Releases on startup; one-click install & restart | Settings → About |

### Sprint Assist Logic
When enabled, Sprint Assist:
1. Immediately holds **W** (forward movement).
2. Holds **Shift** for the configured **Sprint Duration** (default 8s).
3. Releases **Shift** for the **Recovery Duration** (default 4s) so stamina regenerates.
4. Repeats the sprint/recovery cycle automatically.
5. If **Pause when dodging** is on and **Ctrl** is pressed, all keys release for 1.0 second, then resume.

---

## Requirements

**End users (download from GitHub):**
- **Windows 10/11** (x64) only
- **No .NET install needed** — releases are self-contained single-file builds

**Developers (building from source):**
- **.NET 8.0+ SDK** — [Download here](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## Download (end users)

1. Open the latest [**GitHub Release**](https://github.com/mwtnov240/PalAssist/releases/latest).
2. Download **`PalAssist.exe`** (direct — no unzip).
3. Run it (Windows may show SmartScreen for unsigned apps → More info → Run anyway).
4. Launch Palworld, press **Insert** for the menu.

Optional: a `.zip` is also attached if you prefer that format.

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
| **Assists** | Work Assist toggle, Sprint Assist toggle + sliders + dodge pause option |
| **AI Assists** | Placeholder — "Coming in a future update" |
| **Settings** | Hotkey rebinding, HUD position preset/drag, About + auto-update |

---

## Auto-update

On startup (when `auto_check_updates` is true), PalAssist checks the latest [GitHub Release](https://github.com/mwtnov240/PalAssist/releases) and **asks** if you want to download and install. Your settings (`config.json`) are kept; only the executable is replaced.

1. On boot: if an update exists → **Yes/No** prompt to install now.
2. Open **Settings → About** anytime → **Check for Updates** (same prompt if available).
3. **Install & Restart** appears after a staged download if you deferred earlier.

Updates are never force-installed without your confirmation.

### Publishing a new update (for maintainers)

1. **Bump version** in `PalAssist.csproj` (`Version` / `InformationalVersion`, e.g. `1.0.2`).
2. **Publish** self-contained single-file (bundles .NET — users need no runtime):

```powershell
dotnet publish PalAssist\PalAssist.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

3. **Optional zip** (for people who prefer it):

```powershell
Compress-Archive -Path publish\win-x64\PalAssist.exe -DestinationPath publish\PalAssist-v1.0.2-win-x64.zip -Force
```

4. **Create a GitHub Release** with tag **`v1.0.2`** (must match the app version) and upload:
   - **`PalAssist.exe`** (primary — direct download)
   - `PalAssist-v1.0.2-win-x64.zip` (optional)

Clients on older versions will offer the update on next launch or manual check. The updater prefers `.exe` assets, then falls back to `.zip`.

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
│   ├── ConfigManager.cs           # JSON config + KeyHelper utility
│   └── UpdateService.cs           # GitHub Releases check / download / apply
│
└── Features/
    ├── IFeature.cs                # Feature interface
    ├── FeatureManager.cs          # Feature registry + 60Hz tick loop
    ├── WorkAssistFeature.cs       # Work Assist (hold F) implementation
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

3. **Add a UI toggle** in `MainWindow.xaml` (copy the Work Assist card block and update names).

4. **(Optional)** Add a hotkey in `AppConfig` and register it in `RegisterConfigHotkeys()`.

---

## Configuration

All settings persist to `config.json`:
```json
{
  "workAssist_enabled": false,
  "sprint_enabled": false,
  "sprint_duration": 8.0,
  "sprint_recovery": 4.0,
  "sprint_pauseDodge": false,
  "hotkey_menu": "Insert",
  "hotkey_workAssist": "F1",
  "hotkey_sprint": "F2",
  "hud_preset": "TopRight",
  "hud_draggable": false,
  "auto_check_updates": true
}
```

---

## License

This project is provided as-is for personal use.
