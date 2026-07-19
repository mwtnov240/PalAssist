# PalAssist 2

**High-reliability external input assists for [Palworld](https://www.pocketpair.jp/palworld) on Windows.**

Hold interact for work stations, keep keys from sticking on alt-tab, optional crosshair and Smart Work pickup — all via simulated keyboard input. **No memory reading, no injection, no network cheats.**

| | |
|---|---|
| **Version** | 2.0.0 |
| **Platform** | Windows 10/11 x64 |
| **Runtime** | Self-contained (.NET 8 bundled) |
| **Updates** | GitHub Releases auto-check |

---

## Features

| Feature | Description |
|---------|-------------|
| **Work Assist** | Holds **F** for work / interact (hotkey rebindable) |
| **Smart Work Assist** (Beta) | On enable: tap F → wait 1s → hold (pickup item on station first) |
| **Focus Lock** | Releases held keys when Palworld is not focused; resumes on return |
| **Crosshair** | Optional center reticle overlay |
| **AFK safety** | Auto-stops assists if Palworld stays closed **10+ minutes** |
| **Emergency Stop** | One click: disable assists + release keys |
| **Config Manager** | Export / import / reset / open config folder |
| **Help hints (?)** | Hover for “what / when to use” |
| **What's Changed** | Full in-app changelog history |
| **Tray** | Optional system tray + minimize-to-tray |
| **Auto-update** | Checks GitHub Releases; asks before install |

---

## Safety guarantee (2.0)

**No stuck keys** after:

- Update install / restart  
- Unhandled exception (**best-effort** release + `PalAssist2.log`)  
- Palworld exit or crash  
- Normal PalAssist exit (window or tray Exit)

Forced **Task Manager end task** cannot run cleanup. Use Focus Lock, AFK safety, and **Stop all** as defense in depth.

See **[STRESS.md](STRESS.md)** for the long-run stress checklist.

---

## Download

Latest release: [github.com/mwtnov240/PalAssist/releases](https://github.com/mwtnov240/PalAssist/releases)

Primary asset: **`PalAssist.exe`** (self-contained).

---

## Build from source

```powershell
# Restore & build
dotnet build PalAssist.csproj -c Release

# Self-contained single-file publish (no .NET install needed on target PC)
dotnet publish PalAssist.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish\win-x64
```

Output: `publish\win-x64\PalAssist.exe`

---

## Project layout

```
/
├── PalAssist.csproj
├── App.xaml / MainWindow.xaml
├── Assets/app.ico            # Application icon
├── Core/                     # Config, input, focus, updates, logging
├── Features/                 # WorkAssist, Sprint (disabled), FeatureManager
├── Win32/                    # P/Invoke
├── CHANGELOG.md
├── STRESS.md
└── README.md
```

---

## Configuration

Settings live in `config.json` next to the executable (auto-created). Use **Settings → Config** to export, import, or reset.

---

## Screenshots

_Add screenshots here (Assists tab, Focus Lock, What's Changed)._

---

## License / disclaimer

External quality-of-life tool only. Use at your own risk. Not affiliated with Pocketpair.
