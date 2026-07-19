using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalAssist.Core
{
    /// <summary>
    /// Persists and loads user settings to/from a JSON config file.
    /// </summary>
    public sealed class ConfigManager
    {
        private static readonly string ConfigPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        /// <summary>
        /// Must allow NaN for unset HUD/menu coordinates (double.NaN is the "use default" sentinel).
        /// Without this, Serialize throws and Save() silently does nothing.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        /// <summary>Current in-memory config.</summary>
        public AppConfig Config { get; private set; } = new();

        /// <summary>Full path to the on-disk config file.</summary>
        public string PathOnDisk => ConfigPath;

        /// <summary>True if config.json existed when <see cref="Load"/> ran (upgrade vs fresh install).</summary>
        public bool ConfigFileExistedOnLoad { get; private set; }

        /// <summary>
        /// Load config from disk. If the file doesn't exist, returns defaults.
        /// </summary>
        public void Load()
        {
            ConfigFileExistedOnLoad = File.Exists(ConfigPath);
            if (!ConfigFileExistedOnLoad)
            {
                Config = new AppConfig();
                return;
            }

            try
            {
                string json = File.ReadAllText(ConfigPath);
                Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                // Upgrading users: don't auto-force the setup wizard
                if (!Config.BetaSetupWizardCompleted)
                    Config.BetaSetupWizardCompleted = true;
                bool migrated = MigrateLegacyHoldEKeys(json, Config);
                migrated |= MigrateFocusLock(json, Config, configFileExisted: true);
                if (string.IsNullOrWhiteSpace(Config.LastSeenVersion))
                    Config.LastSeenVersion = ""; // first run of this field — What's New skipped until set after boot
                if (migrated)
                    Save();
            }
            catch
            {
                Config = new AppConfig();
                // Treat corrupt file as existing user — no wizard spam
                Config.BetaSetupWizardCompleted = true;
            }
        }

        /// <summary>
        /// Maps beta_focusLock → focus_lock_enabled when the stable key is absent.
        /// Existing installs without either key keep Focus Lock off (opt-in on upgrade).
        /// New installs use AppConfig default (on).
        /// </summary>
        private static bool MigrateFocusLock(string json, AppConfig cfg, bool configFileExisted)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("focus_lock_enabled", out _))
                    return false;

                if (root.TryGetProperty("beta_focusLock", out var beta))
                {
                    cfg.FocusLockEnabled = beta.GetBoolean();
                    return true;
                }

                if (configFileExisted)
                {
                    cfg.FocusLockEnabled = false;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Maps old holdE_* config keys to workAssist_* when the new keys are absent.
        /// Returns true if any migration was applied.
        /// </summary>
        private static bool MigrateLegacyHoldEKeys(string json, AppConfig cfg)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool changed = false;

                bool hasNewEnabled = root.TryGetProperty("workAssist_enabled", out _);
                if (!hasNewEnabled && root.TryGetProperty("holdE_enabled", out var oldEn))
                {
                    cfg.WorkAssistEnabled = oldEn.GetBoolean();
                    changed = true;
                }

                bool hasNewHud = root.TryGetProperty("workAssist_showHud", out _);
                if (!hasNewHud && root.TryGetProperty("holdE_showHud", out var oldHud))
                {
                    cfg.WorkAssistShowHud = oldHud.GetBoolean();
                    changed = true;
                }

                bool hasNewHotkey = root.TryGetProperty("hotkey_workAssist", out _);
                if (!hasNewHotkey && root.TryGetProperty("hotkey_holdE", out var oldHk))
                {
                    string? s = oldHk.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        cfg.HotkeyWorkAssist = s;
                        changed = true;
                    }
                }

                return changed;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Persist the current config to disk.
        /// Returns true on success; false if serialization or write fails.
        /// </summary>
        public bool Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Config, JsonOptions);
                // Atomic-ish write: avoid truncated config if the process dies mid-write
                string tempPath = ConfigPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Copy(tempPath, ConfigPath, overwrite: true);
                File.Delete(tempPath);
                return true;
            }
            catch
            {
                // Overlay must not crash on config I/O failure; callers may show feedback.
                return false;
            }
        }

        /// <summary>Directory containing config.json.</summary>
        public string ConfigDirectory =>
            Path.GetDirectoryName(ConfigPath) ?? AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>Export current in-memory config to a file path.</summary>
        public bool ExportTo(string path)
        {
            try
            {
                string json = JsonSerializer.Serialize(Config, JsonOptions);
                File.WriteAllText(path, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Load config from an external file into memory and save as active config.
        /// Returns false if the file is missing, unreadable, or invalid.
        /// </summary>
        public bool ImportFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path);
                var imported = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (imported == null) return false;
                Config = imported;
                return Save();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Replace config with defaults and persist.</summary>
        public bool ResetToDefaults()
        {
            Config = new AppConfig();
            return Save();
        }
    }

    /// <summary>
    /// Data class holding all persistent settings.
    /// Add new fields here as features are added — they'll automatically serialize.
    /// </summary>
    public class AppConfig
    {
        // ── Work Assist (holds F) ──
        [JsonPropertyName("workAssist_enabled")]
        public bool WorkAssistEnabled { get; set; } = false;

        [JsonPropertyName("workAssist_showHud")]
        public bool WorkAssistShowHud { get; set; } = true;

        // ── Focus Lock (stable; graduated from Beta) ──
        /// <summary>Release held keys when Palworld is not focused. Default on for new installs.</summary>
        [JsonPropertyName("focus_lock_enabled")]
        public bool FocusLockEnabled { get; set; } = true;

        // ── Sprint Assist ──
        [JsonPropertyName("sprint_enabled")]
        public bool SprintEnabled { get; set; } = false;

        [JsonPropertyName("sprint_duration")]
        public double SprintDuration { get; set; } = 8.0;

        [JsonPropertyName("sprint_recovery")]
        public double RecoveryDuration { get; set; } = 4.0;

        [JsonPropertyName("sprint_pauseDodge")]
        public bool SprintPauseDodge { get; set; } = false;

        // ── HUD ──
        [JsonPropertyName("show_hud")]
        public bool ShowHud { get; set; } = true;

        [JsonPropertyName("hud_preset")]
        public string HudPreset { get; set; } = "TopRight";

        [JsonPropertyName("hud_draggable")]
        public bool HudDraggable { get; set; } = false;

        [JsonPropertyName("hud_x")]
        public double HudX { get; set; } = double.NaN;

        [JsonPropertyName("hud_y")]
        public double HudY { get; set; } = double.NaN;

        // ── Menu position ──
        [JsonPropertyName("menu_x")]
        public double MenuX { get; set; } = double.NaN;

        [JsonPropertyName("menu_y")]
        public double MenuY { get; set; } = double.NaN;

        // ── Hotkeys ──
        [JsonPropertyName("hotkey_menu")]
        public string HotkeyMenu { get; set; } = "Insert";

        [JsonPropertyName("hotkey_workAssist")]
        public string HotkeyWorkAssist { get; set; } = "F1";

        [JsonPropertyName("hotkey_sprint")]
        public string HotkeySprint { get; set; } = "F2";

        // ── Updates ──
        [JsonPropertyName("auto_check_updates")]
        public bool AutoCheckUpdates { get; set; } = true;

        /// <summary>
        /// Last app version for which the What's New dialog was shown.
        /// Empty = never shown (set to current on first launch without popup).
        /// </summary>
        [JsonPropertyName("last_seen_version")]
        public string LastSeenVersion { get; set; } = "";

        // ── Appearance ──
        [JsonPropertyName("ui_opacity")]
        public double UiOpacity { get; set; } = 0.94;

        [JsonPropertyName("ui_scale")]
        public double UiScale { get; set; } = 1.0;

        /// <summary>Cyan | Purple | Green | Amber | Red</summary>
        [JsonPropertyName("ui_theme")]
        public string UiTheme { get; set; } = "Cyan";

        // ── Sound ──
        [JsonPropertyName("sound_enabled")]
        public bool SoundEnabled { get; set; } = true;

        [JsonPropertyName("sound_on_toggle")]
        public bool SoundOnToggle { get; set; } = true;

        [JsonPropertyName("sound_on_focus")]
        public bool SoundOnFocus { get; set; } = false;

        [JsonPropertyName("sound_on_update")]
        public bool SoundOnUpdate { get; set; } = true;

        // ── Tray ──
        [JsonPropertyName("tray_enabled")]
        public bool TrayEnabled { get; set; } = true;

        [JsonPropertyName("minimize_to_tray")]
        public bool MinimizeToTray { get; set; } = true;

        // ── Crosshair ──
        [JsonPropertyName("crosshair_enabled")]
        public bool CrosshairEnabled { get; set; } = false;

        /// <summary>Dot | Plus | X</summary>
        [JsonPropertyName("crosshair_style")]
        public string CrosshairStyle { get; set; } = "Dot";

        /// <summary>Small | Medium | Large</summary>
        [JsonPropertyName("crosshair_size")]
        public string CrosshairSize { get; set; } = "Medium";

        /// <summary>White | Cyan | Red | Green | Yellow</summary>
        [JsonPropertyName("crosshair_color")]
        public string CrosshairColor { get; set; } = "White";

        // ── Beta (experimental; off by default) ──
        /// <summary>When false, Beta tab is hidden and beta features stay disabled.</summary>
        [JsonPropertyName("beta_enabled")]
        public bool BetaEnabled { get; set; } = false;

        [JsonPropertyName("beta_focusLock")]
        public bool BetaFocusLock { get; set; } = false;

        [JsonPropertyName("beta_profileWorkEnabled")]
        public bool BetaProfileWorkEnabled { get; set; } = false;

        [JsonPropertyName("beta_activeProfile")]
        public string BetaActiveProfile { get; set; } = "Interact";

        /// <summary>Comma-separated key names for Custom profile, e.g. "W,F".</summary>
        [JsonPropertyName("beta_customKeys")]
        public string BetaCustomKeys { get; set; } = "F";

        [JsonPropertyName("beta_setupWizardCompleted")]
        public bool BetaSetupWizardCompleted { get; set; } = false;

        /// <summary>
        /// When true (and Beta is unlocked), Work Assist taps F once, waits 1s, then holds
        /// so items on workstations are picked up first.
        /// </summary>
        [JsonPropertyName("beta_smartWorkAssist")]
        public bool BetaSmartWorkAssist { get; set; } = false;

        // ── AFK safety ──
        /// <summary>
        /// When true, disable all assists if Palworld stays closed for 10 minutes.
        /// Default on for new installs.
        /// </summary>
        [JsonPropertyName("afk_safety_enabled")]
        public bool AfkSafetyEnabled { get; set; } = true;
    }

    /// <summary>
    /// Maps human-readable key names to Win32 virtual-key codes and back.
    /// Supports all common keys a user might want to bind.
    /// </summary>
    public static class KeyHelper
    {
        private static readonly Dictionary<string, uint> NameToVk = new(StringComparer.OrdinalIgnoreCase)
        {
            // Function keys
            ["F1"]  = 0x70, ["F2"]  = 0x71, ["F3"]  = 0x72, ["F4"]  = 0x73,
            ["F5"]  = 0x74, ["F6"]  = 0x75, ["F7"]  = 0x76, ["F8"]  = 0x77,
            ["F9"]  = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,

            // Navigation / editing
            ["Insert"]   = 0x2D, ["Delete"]   = 0x2E,
            ["Home"]     = 0x24, ["End"]      = 0x23,
            ["PageUp"]   = 0x21, ["PageDown"] = 0x22,
            ["Pause"]    = 0x13, ["ScrollLock"] = 0x91,

            // Numpad
            ["Num0"] = 0x60, ["Num1"] = 0x61, ["Num2"] = 0x62, ["Num3"] = 0x63,
            ["Num4"] = 0x64, ["Num5"] = 0x65, ["Num6"] = 0x66, ["Num7"] = 0x67,
            ["Num8"] = 0x68, ["Num9"] = 0x69,
            ["NumMul"] = 0x6A, ["NumAdd"] = 0x6B, ["NumSub"] = 0x6D, ["NumDot"] = 0x6E, ["NumDiv"] = 0x6F,

            // Misc
            ["Backspace"] = 0x08, ["Tab"] = 0x09, ["CapsLock"] = 0x14,
            ["Space"]     = 0x20, ["Enter"] = 0x0D,

            // Arrow keys
            ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,

            // OEM
            ["Tilde"]        = 0xC0,  // ` ~
            ["Minus"]        = 0xBD,  // - _
            ["Equals"]       = 0xBB,  // = +
            ["LeftBracket"]  = 0xDB,  // [ {
            ["RightBracket"] = 0xDD,  // ] }
            ["Backslash"]    = 0xDC,  // \ |
            ["Semicolon"]    = 0xBA,  // ; :
            ["Quote"]        = 0xDE,  // ' "
            ["Comma"]        = 0xBC,  // , <
            ["Period"]       = 0xBE,  // . >
            ["Slash"]        = 0xBF,  // / ?
        };

        // Reverse map built once
        private static readonly Dictionary<uint, string> VkToName;

        static KeyHelper()
        {
            // Add letter keys A-Z (VK 0x41 – 0x5A)
            for (char c = 'A'; c <= 'Z'; c++)
                NameToVk[c.ToString()] = (uint)c;

            // Add digit keys 0-9 (VK 0x30 – 0x39)
            for (char c = '0'; c <= '9'; c++)
                NameToVk[c.ToString()] = (uint)c;

            // Build reverse map
            VkToName = new Dictionary<uint, string>();
            foreach (var kv in NameToVk)
            {
                // First entry wins for reverse lookup
                if (!VkToName.ContainsKey(kv.Value))
                    VkToName[kv.Value] = kv.Key;
            }
        }

        /// <summary>Convert a key name to a VK code. Returns 0 on failure.</summary>
        public static uint ToVk(string name) =>
            NameToVk.TryGetValue(name, out var vk) ? vk : 0;

        /// <summary>Convert a VK code to a display name. Returns "?" on failure.</summary>
        public static string ToName(uint vk) =>
            VkToName.TryGetValue(vk, out var name) ? name : "?";

        /// <summary>Convert a WPF Key enum to a VK code. Returns 0 if unmapped.</summary>
        public static uint WpfKeyToVk(System.Windows.Input.Key key)
        {
            // KeyInterop gives us the Win32 VK code directly
            int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
            return vk > 0 ? (uint)vk : 0;
        }

        /// <summary>Convert a WPF Key enum to a display name.</summary>
        public static string WpfKeyToName(System.Windows.Input.Key key)
        {
            uint vk = WpfKeyToVk(key);
            return vk > 0 ? ToName(vk) : key.ToString();
        }
    }
}

