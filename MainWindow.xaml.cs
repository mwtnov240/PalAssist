using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PalAssist.Core;
using PalAssist.Features;
using PalAssist.Win32;
// Prefer WPF types when WinForms is also referenced
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace PalAssist
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Sprint Assist is turned off for v1.1. Set true to restore UI, hotkey, and feature.
        /// </summary>
        private static readonly bool SprintAssistAvailable = false;

        // ── Managers ──
        private HotkeyManager?  _hotkeyManager;
        private WindowTracker?  _windowTracker;
        private FeatureManager? _featureManager;
        private ConfigManager?  _configManager;
        private UpdateService?  _updateService;
        private SoundService    _soundService = new();
        private TrayService?    _trayService;
        private CancellationTokenSource? _updateCts;
        private bool _updateInProgress;

        // ── Features ──
        private WorkAssistFeature?         _workAssist;
        private SprintAssistFeature?  _sprint;

        // ── State ──
        private bool   _menuVisible = false;
        private IntPtr _hwnd;
        private bool   _allowClose;
        private bool   _suppressAppearanceHandlers;
        private bool   _suppressSoundHandlers;
        private bool   _suppressTrayHandlers;
        private bool   _suppressFocusLockHandler;

        // Focus Lock: suspend immediately, resume after short debounce
        private DispatcherTimer? _focusResumeTimer;
        private bool _focusLockWantSuspend;

        // ── Hotkey IDs ──
        private int _menuHotkeyId   = -1;
        private int _workAssistHotkeyId  = -1;
        private int _sprintHotkeyId = -1;

        // ── Rebind state ──
        private string? _rebindTarget = null;

        // ── Menu cursor force (ShowCursor ref-count balance) ──
        private int _menuCursorForceCount;
        private bool _menuCursorForced;

        // ── Menu drag state ──
        private bool _isDragging;
        private Point _dragStart;
        private double _dragStartLeft, _dragStartTop;

        // ── HUD drag state ──
        private bool _isHudDragging;
        private Point _hudDragStart;
        private double _hudDragStartLeft, _hudDragStartTop;

        // ── Sprint status update timer ──
        private DispatcherTimer? _uiTimer;

        // ── UI restore guards (avoid recursive Checked handlers) ──
        private bool _suppressBetaEnabledHandler;
        private bool _suppressCrosshairHandlers;
        private bool _suppressAfkSafetyHandler;
        private string _activeTab = "assists";

        // ── AFK safety (Palworld closed ≥ 10 minutes) ──
        private DateTime? _gameMissingSinceUtc;
        private bool _afkSafetyFired;
        private const double AfkSafetyMinutes = 10.0;

        public MainWindow()
        {
            InitializeComponent();
            Loaded  += OnLoaded;
            Closing += OnClosing;
        }

        // ─────────────────────────────────────────────────
        //  Initialisation
        // ─────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            // No-activate (don't steal game focus). Do NOT set WS_EX_TOOLWINDOW —
            // that hides the app from the taskbar. Initially click-through.
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_NOACTIVATE;
            exStyle &= ~NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
            SetClickThrough(true);

            // ── Config ──
            _configManager = new ConfigManager();
            _configManager.Load();
            var cfg = _configManager.Config;

            // ── Theme / appearance (before UI sync that depends on brushes) ──
            ThemeService.Apply(cfg.UiTheme);
            ApplySoundFromConfig(cfg);

            // ── Features ──
            _featureManager = new FeatureManager();

            _workAssist = new WorkAssistFeature();
            _featureManager.Register(_workAssist);

            if (SprintAssistAvailable)
            {
                _sprint = new SprintAssistFeature
                {
                    SprintDurationSec  = cfg.SprintDuration,
                    RecoveryDurationSec = cfg.RecoveryDuration,
                    PauseDodge         = cfg.SprintPauseDodge
                };
                _sprint.StateChanged += () => UiPost(SyncSprintStatus);
                _featureManager.Register(_sprint);
            }
            else
            {
                // Ensure a previous install cannot leave sprint "on" in config
                cfg.SprintEnabled = false;
            }

            // Work Profiles removed — force off leftover config
            cfg.BetaProfileWorkEnabled = false;

            _featureManager.StateChanged += () => UiPost(SyncUI);
            _featureManager.Start();

            // ── Window tracker ──
            _windowTracker = new WindowTracker();
            _windowTracker.BoundsChanged += rect => UiPost(() => AlignToGame(rect));
            _windowTracker.GameDetected  += found => UiPost(() => OnGameDetected(found));
            _windowTracker.FocusChanged  += focused => UiPost(() => OnGameFocusChanged(focused));
            _windowTracker.Start();

            // ── WndProc hook ──
            var source = HwndSource.FromHwnd(_hwnd);
            source?.AddHook(WndProc);

            // ── Hotkeys ──
            _hotkeyManager = new HotkeyManager(_hwnd);
            RegisterConfigHotkeys();

            // ── Restore UI state from config ──
            SprintDurSlider.Value   = cfg.SprintDuration;
            RecoveryDurSlider.Value = cfg.RecoveryDuration;
            PauseDodgeCheck.IsChecked = cfg.SprintPauseDodge;
            HudDraggableToggle.IsChecked = cfg.HudDraggable;
            WorkAssistShowHudCheck.IsChecked = cfg.WorkAssistShowHud;

            // Restore HUD preset combo selection
            SetHudPresetCombo(cfg.HudPreset);

            ApplySprintAssistAvailabilityUi();

            if (cfg.WorkAssistEnabled) { _featureManager.Toggle(_workAssist); WorkAssistToggle.IsChecked = true; }
            if (SprintAssistAvailable && cfg.SprintEnabled && _sprint != null)
            {
                _featureManager.Toggle(_sprint);
                SprintToggle.IsChecked = true;
            }

            // ── Listen for rebind keypresses ──
            PreviewKeyDown += OnPreviewKeyDown;

            // ── Set active tab ──
            SwitchTab("assists");

            // ── Position ──
            this.Left   = 0;
            this.Top    = 0;
            this.Width  = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;

            this.SizeChanged += (_, _) => { PositionHud(); PositionCrosshair(); ClampMenuToCanvas(); };

            // ── UI timer: sprint status + AFK safety poll ──
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _uiTimer.Tick += (_, _) =>
            {
                try
                {
                    SyncSprintStatus();
                    TickAfkSafety();
                }
                catch (Exception ex)
                {
                    AppLog.Error("MainWindow.UiTimer", ex.Message, ex);
                }
            };
            _uiTimer.Start();

            // ── Focus Lock (stable) ──
            _focusResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _focusResumeTimer.Tick += FocusResumeTimer_Tick;
            RestoreFocusLockFromConfig(cfg);

            // ── AFK safety UI ──
            SetAfkSafetyToggleSilent(cfg.AfkSafetyEnabled);
            if (_windowTracker?.IsFound != true)
                _gameMissingSinceUtc = DateTime.UtcNow;

            // ── Beta gate + UI restore (only apply beta features if unlocked) ──
            ApplyBetaTabVisibility(cfg.BetaEnabled);
            SetBetaEnabledToggleSilent(cfg.BetaEnabled);
            if (cfg.BetaEnabled)
                RestoreBetaUiFromConfig(cfg);

            // Crash / emergency release callback while this window is alive
            EmergencyRelease.SetCallback(() =>
            {
                try
                {
                    if (Dispatcher.CheckAccess())
                        StopAllAssists(playSound: false, notify: false);
                    else
                        Dispatcher.Invoke(() => StopAllAssists(playSound: false, notify: false),
                            DispatcherPriority.Send);
                }
                catch
                {
                    try { _featureManager?.ReleaseAllInput(); }
                    catch { FeatureManager.ForceReleaseCommonKeys(); }
                }
            });
            EmergencyRelease.SetStateSnapshot(() =>
            {
                try
                {
                    bool found = _windowTracker?.IsFound == true;
                    bool focused = _windowTracker?.IsFocused == true;
                    bool work = _workAssist?.IsEnabled == true;
                    bool susp = _featureManager?.IsInputSuspended == true;
                    return $"found={found} focused={focused} work={work} suspended={susp}";
                }
                catch { return "(snapshot failed)"; }
            });
            AppLog.Info("MainWindow", "Loaded PalAssist 2");

            // ── Crosshair restore ──
            RestoreCrosshairFromConfig(cfg);

            // ── Appearance / sound / tray UI ──
            RestoreAppearanceFromConfig(cfg);
            RestoreSoundUiFromConfig(cfg);
            RestoreTrayUiFromConfig(cfg);
            InitTray(cfg);

            // Start with menu visible by default on startup
            SetMenuVisible(true);

            UpdateHud();
            PositionHud();
            PositionCrosshair();

            // Restore menu position if saved, otherwise centre it
            if (!double.IsNaN(cfg.MenuX) && !double.IsNaN(cfg.MenuY))
            {
                Canvas.SetLeft(MenuPanel, cfg.MenuX);
                Canvas.SetTop(MenuPanel, cfg.MenuY);
            }
            else
            {
                Dispatcher.InvokeAsync(CentreMenuIfNeeded, DispatcherPriority.Loaded);
            }


            // ── Updates ──
            _updateService = new UpdateService();
            RefreshVersionLabels();
            UpdateStatusText.Text = "";
            InstallUpdateBtn.Visibility = Visibility.Collapsed;
            InstallUpdateBtn.IsEnabled = false;

            // What's New after upgrade (before update check so user sees local notes)
            MaybeShowWhatsNew(cfg);

            // Always auto-check on boot; prompt before download/install
            if (cfg.AutoCheckUpdates)
            {
                _ = BootUpdateCheckAsync();
            }

            BuildChangelogPanel();
        }

        /// <summary>
        /// Keep header, About, and taskbar title on the same assembly version.
        /// </summary>
        private void RefreshVersionLabels()
        {
            string v = UpdateService.GetCurrentVersion();
            HeaderVersionText.Text = $"v{v} — PalAssist 2";
            VersionText.Text = $"PalAssist 2  v{v}";
            Title = $"PalAssist 2  v{v}";
        }

        /// <summary>Fire-and-forget UI work from thread-pool callbacks (never block the timer).</summary>
        private void UiPost(Action action)
        {
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    try { action(); }
                    catch (Exception ex) { AppLog.Error("MainWindow.UiPost", ex.Message, ex); }
                    return;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { action(); }
                    catch (Exception ex) { AppLog.Error("MainWindow.UiPost", ex.Message, ex); }
                }), DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                AppLog.Error("MainWindow.UiPost.Marshal", ex.Message, ex);
            }
        }

        // ─────────────────────────────────────────────────
        //  Tab switching
        // ─────────────────────────────────────────────────

        private void TabAssists_Click(object s, RoutedEventArgs e) => SwitchTab("assists");
        private void TabBeta_Click(object s, RoutedEventArgs e)    => SwitchTab("beta");
        private void TabSettings_Click(object s, RoutedEventArgs e) => SwitchTab("settings");
        private void TabChangelog_Click(object s, RoutedEventArgs e) => SwitchTab("changelog");

        private void SwitchTab(string tab)
        {
            // Beta tab requires opt-in in Settings
            if (tab == "beta" && _configManager?.Config.BetaEnabled != true)
            {
                tab = "assists";
            }

            _activeTab = tab;

            PanelAssists.Visibility   = tab == "assists"   ? Visibility.Visible : Visibility.Collapsed;
            PanelBeta.Visibility      = tab == "beta"      ? Visibility.Visible : Visibility.Collapsed;
            PanelSettings.Visibility  = tab == "settings"  ? Visibility.Visible : Visibility.Collapsed;
            PanelChangelog.Visibility = tab == "changelog" ? Visibility.Visible : Visibility.Collapsed;

            // Vertical rail: cyan left border + accent text when active
            var cyanBrush = (SolidColorBrush)FindResource("AccentCyanBrush");
            var transBrush = Brushes.Transparent;
            var cyanFg = cyanBrush;
            var secFg  = (SolidColorBrush)FindResource("TextSecondaryBrush");

            void StyleTab(Button btn, bool active)
            {
                btn.BorderBrush = active ? cyanBrush : transBrush;
                btn.Foreground = active ? cyanFg : secFg;
            }

            StyleTab(TabAssists, tab == "assists");
            StyleTab(TabBeta, tab == "beta");
            StyleTab(TabSettings, tab == "settings");
            StyleTab(TabChangelog, tab == "changelog");
        }

        // ─────────────────────────────────────────────────
        //  WndProc hook
        // ─────────────────────────────────────────────────

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_hotkeyManager != null && _hotkeyManager.ProcessMessage(msg, wParam))
                handled = true;
            return IntPtr.Zero;
        }

        // ─────────────────────────────────────────────────
        //  Hotkey registration (config-driven)
        // ─────────────────────────────────────────────────

        private void RegisterConfigHotkeys()
        {
            if (_hotkeyManager == null || _configManager == null) return;
            var cfg = _configManager.Config;

            uint menuVk = KeyHelper.ToVk(cfg.HotkeyMenu);
            if (menuVk != 0) _menuHotkeyId = _hotkeyManager.Register(menuVk, 0, OnMenuHotkeyPressed);

            uint workAssistVk = KeyHelper.ToVk(cfg.HotkeyWorkAssist);
            if (workAssistVk != 0) _workAssistHotkeyId = _hotkeyManager.Register(workAssistVk, 0, OnWorkAssistHotkeyPressed);

            if (SprintAssistAvailable)
            {
                uint sprintVk = KeyHelper.ToVk(cfg.HotkeySprint);
                if (sprintVk != 0) _sprintHotkeyId = _hotkeyManager.Register(sprintVk, 0, OnSprintHotkeyPressed);
            }

            RefreshHotkeyLabels();
        }

        private void ApplySprintAssistAvailabilityUi()
        {
            var vis = SprintAssistAvailable ? Visibility.Visible : Visibility.Collapsed;
            SprintAssistCard.Visibility = vis;
            SprintHotkeyRow.Visibility = vis;
        }

        private void RefreshHotkeyLabels()
        {
            if (_configManager == null) return;
            var cfg = _configManager.Config;

            RebindMenuBtn.Content    = cfg.HotkeyMenu;
            RebindWorkAssistBtn.Content   = cfg.HotkeyWorkAssist;
            RebindSprintBtn.Content  = cfg.HotkeySprint;

            WorkAssistSubtitle.Text   = $"Holds F  ·  Hotkey: {cfg.HotkeyWorkAssist}";
            SprintSubtitle.Text  = $"Auto forward + sprint cycles  ·  Hotkey: {cfg.HotkeySprint}";

            FooterMenuKey.Text   = cfg.HotkeyMenu;
            FooterWorkAssistKey.Text  = cfg.HotkeyWorkAssist;

            if (SprintAssistAvailable)
            {
                FooterSprintSep.Text = "  ·  ";
                FooterSprintKey.Text = cfg.HotkeySprint;
                FooterSprintLabel.Text = " sprint";
            }
            else
            {
                FooterSprintSep.Text = "";
                FooterSprintKey.Text = "";
                FooterSprintLabel.Text = "";
            }
        }

        // ─────────────────────────────────────────────────
        //  Hotkey callbacks
        // ─────────────────────────────────────────────────

        private void OnMenuHotkeyPressed()
        {
            SetMenuVisible(!_menuVisible);
        }

        /// <summary>
        /// Show/hide the main menu card and force a system cursor while it is open
        /// (HUD-only does not force the cursor).
        /// </summary>
        private void SetMenuVisible(bool visible)
        {
            _menuVisible = visible;
            MenuPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SetClickThrough(!visible);
            SetMenuCursorForced(visible);
            if (visible) CentreMenuIfNeeded();
        }

        private void SetMenuCursorForced(bool force)
        {
            if (force)
            {
                if (_menuCursorForced) return;
                _menuCursorForced = true;
                Mouse.OverrideCursor = Cursors.Arrow;
                Cursor = Cursors.Arrow;

                // Windows uses a display counter; ensure it is visible while menu is open
                _menuCursorForceCount = 0;
                for (int i = 0; i < 16; i++)
                {
                    int count = NativeMethods.ShowCursor(true);
                    _menuCursorForceCount++;
                    if (count >= 0) break;
                }
            }
            else
            {
                if (!_menuCursorForced) return;
                _menuCursorForced = false;
                Mouse.OverrideCursor = null;
                Cursor = Cursors.Arrow;

                for (int i = 0; i < _menuCursorForceCount; i++)
                    NativeMethods.ShowCursor(false);
                _menuCursorForceCount = 0;
            }
        }

        private void OnWorkAssistHotkeyPressed()
        {
            if (_workAssist == null || _featureManager == null) return;
            _featureManager.Toggle(_workAssist);
            WorkAssistToggle.IsChecked = _workAssist.IsEnabled;
            _soundService.PlayToggle(_workAssist.IsEnabled);
        }

        private void OnSprintHotkeyPressed()
        {
            if (!SprintAssistAvailable || _sprint == null || _featureManager == null) return;
            _featureManager.Toggle(_sprint);
            SprintToggle.IsChecked = _sprint.IsEnabled;
            _soundService.PlayToggle(_sprint.IsEnabled);
        }

        // ─────────────────────────────────────────────────
        //  Rebind flow
        // ─────────────────────────────────────────────────

        private void RebindMenuBtn_Click(object s, RoutedEventArgs e)   => StartRebind("menu");
        private void RebindWorkAssistBtn_Click(object s, RoutedEventArgs e)  => StartRebind("workAssist");
        private void RebindSprintBtn_Click(object s, RoutedEventArgs e) => StartRebind("sprint");

        private bool _rebindClearedNoActivate;

        private void StartRebind(string target)
        {
            _rebindTarget = target;
            var btn = target switch
            {
                "menu" => RebindMenuBtn,
                "workAssist" => RebindWorkAssistBtn,
                _ => RebindSprintBtn
            };
            btn.Content = "Press a key…";
            BeginKeyCapture();
        }

        private void BeginKeyCapture()
        {
            // Temporarily allow the overlay to be activated/focused so PreviewKeyDown fires.
            // Without this, WS_EX_NOACTIVATE silently blocks all keyboard input.
            try
            {
                int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
                exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
                NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
                _rebindClearedNoActivate = true;
                this.Activate();
                this.Focus();
            }
            catch (Exception ex)
            {
                AppLog.Error("MainWindow.BeginKeyCapture", ex.Message, ex);
                RestoreNoActivateStyle();
            }
        }

        /// <summary>Always restore WS_EX_NOACTIVATE after rebind (try/finally safety).</summary>
        private void RestoreNoActivateStyle()
        {
            try
            {
                if (!_rebindClearedNoActivate && _hwnd == IntPtr.Zero) return;
                int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
                exStyle |= NativeMethods.WS_EX_NOACTIVATE;
                NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
            }
            catch (Exception ex)
            {
                AppLog.Error("MainWindow.RestoreNoActivate", ex.Message, ex);
            }
            finally
            {
                _rebindClearedNoActivate = false;
            }
        }

        /// <summary>Restores WS_EX_NOACTIVATE after a rebind completes or is cancelled.</summary>
        private void EndRebind()
        {
            _rebindTarget = null;
            RestoreNoActivateStyle();
            try { RefreshHotkeyLabels(); }
            catch (Exception ex) { AppLog.Error("MainWindow.EndRebind", ex.Message, ex); }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_rebindTarget == null) return;
            try
            {
                Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
                if (key == Key.LeftShift || key == Key.RightShift ||
                    key == Key.LeftCtrl  || key == Key.RightCtrl  ||
                    key == Key.LeftAlt   || key == Key.RightAlt   ||
                    key == Key.LWin      || key == Key.RWin) return;

                e.Handled = true;

                if (key == Key.Escape)
                {
                    EndRebind();
                    return;
                }

                uint vk = KeyHelper.WpfKeyToVk(key);
                string name = KeyHelper.WpfKeyToName(key);

                if (vk == 0)
                {
                    EndRebind();
                    return;
                }

                string savedTarget = _rebindTarget!;

                ref int id = ref _menuHotkeyId;
                if (savedTarget == "workAssist")  id = ref _workAssistHotkeyId;
                if (savedTarget == "sprint") id = ref _sprintHotkeyId;

                // Restore NOACTIVATE before re-registering hotkeys
                EndRebind();
                ApplyRebind(ref id, vk, name, savedTarget);
            }
            catch (Exception ex)
            {
                AppLog.Error("MainWindow.OnPreviewKeyDown", ex.Message, ex);
                EndRebind();
            }
        }

        private void ApplyRebind(ref int hotkeyId, uint newVk, string newName, string target)
        {
            if (_hotkeyManager == null || _configManager == null) return;
            if (hotkeyId >= 0) _hotkeyManager.Unregister(hotkeyId);

            Action cb = target switch
            {
                "menu"  => OnMenuHotkeyPressed,
                "workAssist" => OnWorkAssistHotkeyPressed,
                _       => OnSprintHotkeyPressed
            };
            hotkeyId = _hotkeyManager.Register(newVk, 0, cb);

            switch (target)
            {
                case "menu":   _configManager.Config.HotkeyMenu   = newName; break;
                case "workAssist":  _configManager.Config.HotkeyWorkAssist  = newName; break;
                case "sprint": _configManager.Config.HotkeySprint = newName; break;
            }
            _configManager.Save();
            RefreshHotkeyLabels();
        }

        // Close button — fully exits PalAssist (Insert hides/shows the menu)
        private void CloseBtn_Click(object s, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // ─────────────────────────────────────────────────
        //  Click-through
        // ─────────────────────────────────────────────────

        private void SetClickThrough(bool clickThrough)
        {
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            if (clickThrough) exStyle |= NativeMethods.WS_EX_TRANSPARENT;
            else              exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        }

        // ─────────────────────────────────────────────────
        //  Game window tracking
        // ─────────────────────────────────────────────────

        private void AlignToGame(NativeMethods.RECT rect)
        {
            this.Left   = rect.Left;
            this.Top    = rect.Top;
            this.Width  = rect.Right  - rect.Left;
            this.Height = rect.Bottom - rect.Top;
            PositionCrosshair();
        }

        private void OnGameDetected(bool found)
        {
            GameDot.Fill = found
                ? (SolidColorBrush)FindResource("AccentGreenBrush")
                : (SolidColorBrush)FindResource("AccentRedBrush");

            if (!found)
            {
                GameStatusText.Text = "Palworld not found";
                if (_gameMissingSinceUtc == null)
                    _gameMissingSinceUtc = DateTime.UtcNow;
                // Focus path also fires; keep status labels in sync
                UpdateFocusLockStatus();
                return;
            }

            _gameMissingSinceUtc = null;
            _afkSafetyFired = false;

            string? platform = _windowTracker?.Platform;
            GameStatusText.Text = string.IsNullOrEmpty(platform)
                ? "Palworld connected"
                : $"Palworld connected ({platform})";
            UpdateFocusLockStatus();
        }

        private void TickAfkSafety()
        {
            if (_configManager?.Config.AfkSafetyEnabled != true) return;
            if (_afkSafetyFired) return;
            if (_gameMissingSinceUtc == null) return;
            if (_windowTracker?.IsFound == true) return;

            var missing = DateTime.UtcNow - _gameMissingSinceUtc.Value;
            if (missing.TotalMinutes < AfkSafetyMinutes) return;

            bool anyOn = (_workAssist?.IsEnabled == true)
                         || (_sprint?.IsEnabled == true);
            if (!anyOn)
            {
                _afkSafetyFired = true;
                return;
            }

            _afkSafetyFired = true;
            StopAllAssists(playSound: true, notify: true);
            try
            {
                _trayService?.ShowBalloon(
                    "PalAssist 2",
                    "Assists stopped — Palworld was closed for 10+ minutes.",
                    4000);
            }
            catch { /* ignore */ }
        }

        /// <summary>Disable all assists, release keys, sync UI toggles.</summary>
        private void StopAllAssists(bool playSound, bool notify)
        {
            try
            {
                _featureManager?.ReleaseAllInput();
            }
            catch
            {
                FeatureManager.ForceReleaseCommonKeys();
            }

            if (WorkAssistToggle.IsChecked == true)
                WorkAssistToggle.IsChecked = false;
            if (SprintToggle.IsChecked == true)
                SprintToggle.IsChecked = false;

            SyncUI();

            if (playSound)
            {
                try { _soundService.PlayToggle(false); } catch { /* ignore */ }
            }

            if (notify && ConfigStatusText != null)
                ConfigStatusText.Text = "Assists stopped (AFK safety or emergency stop).";
        }

        private void StopAllAssistsBtn_Click(object s, RoutedEventArgs e)
        {
            StopAllAssists(playSound: true, notify: true);
            if (ConfigStatusText != null)
                ConfigStatusText.Text = "All assists stopped and keys released.";
        }

        private void SetAfkSafetyToggleSilent(bool enabled)
        {
            _suppressAfkSafetyHandler = true;
            try
            {
                AfkSafetyToggle.IsChecked = enabled;
            }
            finally
            {
                _suppressAfkSafetyHandler = false;
            }
        }

        private void AfkSafetyToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressAfkSafetyHandler || _configManager == null) return;
            _configManager.Config.AfkSafetyEnabled = AfkSafetyToggle.IsChecked == true;
            _configManager.Save();
            if (AfkSafetyToggle.IsChecked != true)
                _afkSafetyFired = false;
        }

        // ─────────────────────────────────────────────────
        //  Feature UI event handlers
        // ─────────────────────────────────────────────────

        private void WorkAssistToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_workAssist == null || _featureManager == null) return;
            bool want = WorkAssistToggle.IsChecked == true;
            if (want != _workAssist.IsEnabled)
            {
                _featureManager.Toggle(_workAssist);
                _soundService.PlayToggle(_workAssist.IsEnabled);
            }
        }

        private void SprintToggle_Changed(object s, RoutedEventArgs e)
        {
            if (!SprintAssistAvailable || _sprint == null || _featureManager == null) return;
            bool want = SprintToggle.IsChecked == true;
            if (want != _sprint.IsEnabled)
            {
                _featureManager.Toggle(_sprint);
                _soundService.PlayToggle(_sprint.IsEnabled);
            }
        }

        private void SprintDurSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sprint == null || _configManager == null) return;
            _sprint.SprintDurationSec = e.NewValue;
            _configManager.Config.SprintDuration = e.NewValue;
            if (SprintDurLabel != null) SprintDurLabel.Text = $"{e.NewValue:F1}s";
        }

        private void RecoveryDurSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sprint == null || _configManager == null) return;
            _sprint.RecoveryDurationSec = e.NewValue;
            _configManager.Config.RecoveryDuration = e.NewValue;
            if (RecoveryDurLabel != null) RecoveryDurLabel.Text = $"{e.NewValue:F1}s";
        }

        private void PauseDodgeCheck_Changed(object s, RoutedEventArgs e)
        {
            if (_sprint == null || _configManager == null) return;
            bool val = PauseDodgeCheck.IsChecked == true;
            _sprint.PauseDodge = val;
            _configManager.Config.SprintPauseDodge = val;
        }

        // ─────────────────────────────────────────────────
        //  HUD Settings handlers
        // ─────────────────────────────────────────────────

        private void HudPresetCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (_configManager == null || HudPresetCombo.SelectedItem == null) return;
            string preset = ((ComboBoxItem)HudPresetCombo.SelectedItem).Content.ToString()!.Replace("-", "");
            _configManager.Config.HudPreset = preset;
            // Clear custom drag offsets when a preset is chosen
            _configManager.Config.HudX = double.NaN;
            _configManager.Config.HudY = double.NaN;
            PositionHud();
        }

        private void HudDraggableToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            _configManager.Config.HudDraggable = HudDraggableToggle.IsChecked == true;
        }

        private void WorkAssistShowHudCheck_Changed(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            _configManager.Config.WorkAssistShowHud = WorkAssistShowHudCheck.IsChecked == true;
            UpdateHud();
        }

        // ─────────────────────────────────────────────────
        //  Beta features
        // ─────────────────────────────────────────────────

        private void ApplyBetaTabVisibility(bool enabled)
        {
            TabBeta.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled && _activeTab == "beta")
                SwitchTab("assists");
        }

        private void SetBetaEnabledToggleSilent(bool enabled)
        {
            _suppressBetaEnabledHandler = true;
            try
            {
                BetaEnabledToggle.IsChecked = enabled;
            }
            finally
            {
                _suppressBetaEnabledHandler = false;
            }
        }

        private void BetaEnabledToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressBetaEnabledHandler || _configManager == null) return;

            bool want = BetaEnabledToggle.IsChecked == true;

            if (want)
            {
                var result = MessageBox.Show(
                    "Enable Beta features?\n\nThese are experimental and may be unstable.",
                    "Enable Beta",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    SetBetaEnabledToggleSilent(false);
                    return;
                }

                _configManager.Config.BetaEnabled = true;
                ApplyBetaTabVisibility(true);
                _configManager.Save();
            }
            else
            {
                _configManager.Config.BetaEnabled = false;
                DisableBetaFeatures();
                ApplyBetaTabVisibility(false);
                _configManager.Save();
            }
        }

        /// <summary>
        /// Turns off beta-only assists and clears their UI when the Beta tab is locked again.
        /// Focus Lock is stable and is not cleared here.
        /// </summary>
        private void DisableBetaFeatures()
        {
            if (_configManager != null)
            {
                _configManager.Config.BetaProfileWorkEnabled = false;
                _configManager.Config.BetaSmartWorkAssist = false;
            }

            BetaSmartWorkAssistToggle.IsChecked = false;
            ApplySmartWorkAssistToFeature(false);
            UpdateHud();
        }

        private void RestoreBetaUiFromConfig(AppConfig cfg)
        {
            BetaSmartWorkAssistToggle.IsChecked = cfg.BetaSmartWorkAssist;
            ApplySmartWorkAssistToFeature(cfg.BetaSmartWorkAssist);
        }

        private void ApplySmartWorkAssistToFeature(bool enabled)
        {
            if (_workAssist != null)
                _workAssist.SmartPickupEnabled = enabled;
        }

        private void BetaSmartWorkAssistToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            bool want = BetaSmartWorkAssistToggle.IsChecked == true
                        && _configManager.Config.BetaEnabled;
            _configManager.Config.BetaSmartWorkAssist = want;
            ApplySmartWorkAssistToFeature(want);
            if (BetaSmartWorkAssistToggle.IsChecked == true && !want)
                BetaSmartWorkAssistToggle.IsChecked = false;
            _configManager.Save();
        }

        private void RestoreFocusLockFromConfig(AppConfig cfg)
        {
            _suppressFocusLockHandler = true;
            try
            {
                FocusLockToggle.IsChecked = cfg.FocusLockEnabled;
            }
            finally
            {
                _suppressFocusLockHandler = false;
            }

            ApplyFocusLockState(cfg.FocusLockEnabled, playSound: false);
            UpdateFocusLockStatus();
        }

        private void SaveBetaConfig()
        {
            if (_configManager == null) return;
            var cfg = _configManager.Config;
            cfg.BetaProfileWorkEnabled = false;
            cfg.BetaSmartWorkAssist = BetaSmartWorkAssistToggle.IsChecked == true
                                     && cfg.BetaEnabled;
            ApplySmartWorkAssistToFeature(cfg.BetaSmartWorkAssist);
            _configManager.Save();
        }

        private void UpdateFocusLockStatus()
        {
            bool lockOn = FocusLockToggle.IsChecked == true;
            bool focused = _windowTracker?.IsFocused == true;
            bool suspended = _featureManager?.IsInputSuspended == true;

            if (!lockOn)
            {
                FocusLockDot.Fill = (SolidColorBrush)FindResource("TextSecondaryBrush");
                FocusLockStatusDot.Fill = (SolidColorBrush)FindResource("TextSecondaryBrush");
                FocusLockStatusText.Text = "Focus lock off";
            }
            else if (suspended || !focused)
            {
                FocusLockDot.Fill = (SolidColorBrush)FindResource("AccentYellowBrush");
                FocusLockStatusDot.Fill = (SolidColorBrush)FindResource("AccentYellowBrush");
                FocusLockStatusText.Text = _windowTracker?.IsFound != true
                    ? "Suspended (Palworld not found)"
                    : "Suspended (Palworld not focused)";
            }
            else
            {
                FocusLockDot.Fill = (SolidColorBrush)FindResource("AccentGreenBrush");
                FocusLockStatusDot.Fill = (SolidColorBrush)FindResource("AccentGreenBrush");
                FocusLockStatusText.Text = "Game focused — assists active";
            }
        }

        private void OnGameFocusChanged(bool focused)
        {
            if (_configManager?.Config.FocusLockEnabled != true || _featureManager == null)
            {
                UpdateFocusLockStatus();
                return;
            }

            // Suspend immediately when unfocused; debounce resume to avoid flicker
            if (!focused)
            {
                _focusLockWantSuspend = true;
                _focusResumeTimer?.Stop();
                if (!_featureManager.IsInputSuspended)
                {
                    _featureManager.SetInputSuspended(true);
                    _soundService.PlayFocus(suspended: true);
                }
            }
            else
            {
                _focusLockWantSuspend = false;
                if (_featureManager.IsInputSuspended)
                {
                    _focusResumeTimer?.Stop();
                    _focusResumeTimer?.Start();
                }
            }

            UpdateFocusLockStatus();
        }

        private void FocusResumeTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                _focusResumeTimer?.Stop();
                if (_focusLockWantSuspend) return;
                if (_configManager?.Config.FocusLockEnabled != true || _featureManager == null) return;
                if (_windowTracker?.IsFocused != true) return;

                if (_featureManager.IsInputSuspended)
                {
                    _featureManager.SetInputSuspended(false);
                    _soundService.PlayFocus(suspended: false);
                }
                UpdateFocusLockStatus();
            }
            catch (Exception ex)
            {
                AppLog.Error("MainWindow.FocusResumeTimer", ex.Message, ex);
            }
        }

        private void FocusLockToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressFocusLockHandler || _configManager == null || _featureManager == null) return;
            bool on = FocusLockToggle.IsChecked == true;
            _configManager.Config.FocusLockEnabled = on;
            _configManager.Save();
            ApplyFocusLockState(on, playSound: true);
            UpdateFocusLockStatus();
        }

        private void ApplyFocusLockState(bool enabled, bool playSound)
        {
            if (_featureManager == null) return;
            _focusResumeTimer?.Stop();
            _focusLockWantSuspend = false;

            if (enabled)
            {
                bool shouldSuspend = _windowTracker?.IsFocused != true;
                _focusLockWantSuspend = shouldSuspend;
                bool was = _featureManager.IsInputSuspended;
                _featureManager.SetInputSuspended(shouldSuspend);
                if (playSound && was != shouldSuspend)
                    _soundService.PlayFocus(suspended: shouldSuspend);
            }
            else
            {
                bool was = _featureManager.IsInputSuspended;
                _featureManager.SetInputSuspended(false);
                if (playSound && was)
                    _soundService.PlayFocus(suspended: false);
            }
        }

        private async void SaveSettingsBtn_Click(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;

            // Sync live UI / feature state into the config object before writing
            var cfg = _configManager.Config;
            if (_workAssist != null) cfg.WorkAssistEnabled = _workAssist.IsEnabled;
            cfg.SprintEnabled = SprintAssistAvailable && _sprint != null && _sprint.IsEnabled;
            cfg.BetaProfileWorkEnabled = false;

            cfg.SprintDuration = SprintDurSlider.Value;
            cfg.RecoveryDuration = RecoveryDurSlider.Value;
            cfg.SprintPauseDodge = PauseDodgeCheck.IsChecked == true;
            cfg.HudDraggable = HudDraggableToggle.IsChecked == true;
            cfg.WorkAssistShowHud = WorkAssistShowHudCheck.IsChecked == true;
            cfg.MenuX = Canvas.GetLeft(MenuPanel);
            cfg.MenuY = Canvas.GetTop(MenuPanel);
            cfg.BetaEnabled = BetaEnabledToggle.IsChecked == true;
            cfg.FocusLockEnabled = FocusLockToggle.IsChecked == true;
            cfg.BetaSmartWorkAssist = BetaSmartWorkAssistToggle.IsChecked == true
                                     && cfg.BetaEnabled;
            ApplySmartWorkAssistToFeature(cfg.BetaSmartWorkAssist);
            cfg.AfkSafetyEnabled = AfkSafetyToggle.IsChecked == true;

            SyncAppearanceConfigFromUi(cfg);
            SyncSoundConfigFromUi(cfg);
            SyncTrayConfigFromUi(cfg);
            SyncCrosshairConfigFromUi(cfg);

            if (HudPresetCombo.SelectedItem != null)
            {
                cfg.HudPreset = ((ComboBoxItem)HudPresetCombo.SelectedItem).Content.ToString()!.Replace("-", "");
            }

            bool ok = _configManager.Save();

            if (ok)
            {
                SaveSettingsBtn.Content = "Settings Saved! ✓";
                SaveSettingsBtn.Background = (SolidColorBrush)FindResource("AccentGreenBrush");
                SaveSettingsBtn.Foreground = Brushes.White;
            }
            else
            {
                SaveSettingsBtn.Content = "Save Failed";
                SaveSettingsBtn.Background = (SolidColorBrush)FindResource("AccentRedBrush");
                SaveSettingsBtn.Foreground = Brushes.White;
            }

            SaveSettingsBtn.IsEnabled = false;
            await Task.Delay(1500);

            SaveSettingsBtn.Content = "Save Settings";
            SaveSettingsBtn.IsEnabled = true;
            SaveSettingsBtn.Background = (SolidColorBrush)FindResource("AccentBlueBrush");
            SaveSettingsBtn.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
        }

        private void SetHudPresetCombo(string preset)
        {
            foreach (ComboBoxItem item in HudPresetCombo.Items)
            {
                string val = item.Content.ToString()!.Replace("-", "");
                if (string.Equals(val, preset, StringComparison.OrdinalIgnoreCase))
                {
                    HudPresetCombo.SelectedItem = item;
                    return;
                }
            }
        }

        // ─────────────────────────────────────────────────
        //  Auto-update (GitHub Releases)
        // ─────────────────────────────────────────────────

        private async void UpdateBtn_Click(object s, RoutedEventArgs e)
        {
            // Manual check: look for update, then prompt to download & install
            await RunUpdateCheckAsync(downloadIfAvailable: false, userInitiated: true);
            if (_updateService?.PendingUpdate is { Success: true, UpdateAvailable: true } pending
                && !_updateService.IsStaged)
            {
                bool yes = PromptInstallUpdate(pending.LatestVersion);
                if (yes)
                    await DownloadAndInstallNowAsync();
            }
            else if (_updateService?.IsStaged == true)
            {
                bool yes = PromptInstallUpdate(_updateService.PendingUpdate?.LatestVersion ?? "?");
                if (yes)
                    ApplyStagedUpdateAndRestart();
            }
        }

        private void InstallUpdateBtn_Click(object s, RoutedEventArgs e)
        {
            ApplyStagedUpdateAndRestart();
        }

        /// <summary>
        /// On boot: check for updates (no silent download). If available, ask the user.
        /// </summary>
        private async Task BootUpdateCheckAsync()
        {
            await RunUpdateCheckAsync(downloadIfAvailable: false, userInitiated: false);
            if (_updateService?.PendingUpdate is not { Success: true, UpdateAvailable: true } pending)
                return;

            bool yes = PromptInstallUpdate(pending.LatestVersion);
            if (yes)
                await DownloadAndInstallNowAsync();
            else
                SetUpdateStatus($"v{pending.LatestVersion} available — use Check for Updates when ready.");
        }

        private bool PromptInstallUpdate(string version)
        {
            _soundService.PlayUpdate();
            string notes = _updateService?.PendingUpdate?.ReleaseNotes?.Trim() ?? "";
            if (notes.Length > 400)
                notes = notes[..400] + "…";

            string body = $"PalAssist v{version} is available.\n\nDownload and install now?\n(The app will restart after installing.)";
            if (!string.IsNullOrWhiteSpace(notes))
                body += "\n\nRelease notes:\n" + notes;

            var result = MessageBox.Show(
                this,
                body,
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        private async Task DownloadAndInstallNowAsync()
        {
            if (_updateService == null) return;

            _updateInProgress = true;
            _updateCts?.Cancel();
            _updateCts = new CancellationTokenSource();
            var ct = _updateCts.Token;

            try
            {
                UpdateBtn.IsEnabled = false;
                UpdateBtn.Content = "Downloading…";
                SetUpdateStatus("Downloading update…");

                var progress = new Progress<double>(p =>
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        UpdateBtn.Content = "Downloading…";
                        SetUpdateStatus($"Downloading update… {p:0}%");
                    });
                });

                // Ensure we have release metadata
                if (_updateService.PendingUpdate is not { UpdateAvailable: true })
                {
                    var check = await _updateService.CheckForUpdateAsync(ct).ConfigureAwait(true);
                    if (!check.Success || !check.UpdateAvailable)
                    {
                        ApplyUpdateUi(check);
                        return;
                    }
                }

                var result = await _updateService.DownloadAndStageAsync(progress, ct).ConfigureAwait(true);
                ApplyUpdateUi(result);

                if (result.Success && (result.Staged || _updateService.IsStaged))
                    ApplyStagedUpdateAndRestart();
            }
            catch (OperationCanceledException)
            {
                SetUpdateStatus("Update download cancelled.");
                UpdateBtn.Content = "Check for Updates";
                UpdateBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                SetUpdateStatus($"Download failed: {ex.Message}");
                UpdateBtn.Content = "Check for Updates";
                UpdateBtn.IsEnabled = true;
            }
            finally
            {
                _updateInProgress = false;
            }
        }

        private void ApplyStagedUpdateAndRestart()
        {
            if (_updateService == null || !_updateService.IsStaged)
            {
                SetUpdateStatus("No update is ready to install.");
                return;
            }

            try
            {
                _featureManager?.DisableAll();

                if (_configManager != null)
                {
                    if (_workAssist != null)  _configManager.Config.WorkAssistEnabled  = false;
                    if (_sprint != null) _configManager.Config.SprintEnabled = false;
                    _configManager.Config.MenuX = Canvas.GetLeft(MenuPanel);
                    _configManager.Config.MenuY = Canvas.GetTop(MenuPanel);
                    _configManager.Save();
                }

                SetUpdateStatus("Installing update… restarting shortly.");
                InstallUpdateBtn.IsEnabled = false;
                UpdateBtn.IsEnabled = false;

                if (!_updateService.ApplyAndRestart())
                {
                    SetUpdateStatus("Could not start the updater. Try running as admin or reinstall.");
                    UpdateBtn.IsEnabled = true;
                    InstallUpdateBtn.IsEnabled = true;
                    return;
                }

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                SetUpdateStatus($"Install failed: {ex.Message}");
                UpdateBtn.IsEnabled = true;
                InstallUpdateBtn.IsEnabled = true;
            }
        }

        private async Task RunUpdateCheckAsync(bool downloadIfAvailable, bool userInitiated)
        {
            if (_updateService == null) return;
            if (_updateInProgress)
            {
                if (userInitiated)
                    SetUpdateStatus("Update check already in progress…");
                return;
            }

            _updateInProgress = true;
            _updateCts?.Cancel();
            _updateCts = new CancellationTokenSource();
            var ct = _updateCts.Token;

            try
            {
                UpdateBtn.IsEnabled = false;
                UpdateBtn.Content = "Checking…";
                if (userInitiated || string.IsNullOrEmpty(UpdateStatusText.Text))
                    SetUpdateStatus("Checking for updates…");

                UpdateCheckResult result;
                if (downloadIfAvailable)
                {
                    var progress = new Progress<double>(p =>
                    {
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            UpdateBtn.Content = "Downloading…";
                            SetUpdateStatus($"Downloading update… {p:0}%");
                        });
                    });

                    result = await _updateService.CheckForUpdateAsync(ct).ConfigureAwait(true);
                    if (result.Success && result.UpdateAvailable)
                    {
                        UpdateBtn.Content = "Downloading…";
                        SetUpdateStatus($"Downloading v{result.LatestVersion}…");
                        result = await _updateService.DownloadAndStageAsync(progress, ct).ConfigureAwait(true);
                    }
                }
                else
                {
                    result = await _updateService.CheckForUpdateAsync(ct).ConfigureAwait(true);
                }

                ApplyUpdateUi(result);
            }
            catch (OperationCanceledException)
            {
                if (userInitiated)
                    SetUpdateStatus("Update check cancelled.");
                UpdateBtn.Content = "Check for Updates";
                UpdateBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                SetUpdateStatus($"Update check failed: {ex.Message}");
                UpdateBtn.Content = "Check for Updates";
                UpdateBtn.IsEnabled = true;
                InstallUpdateBtn.Visibility = Visibility.Collapsed;
                InstallUpdateBtn.IsEnabled = false;
            }
            finally
            {
                _updateInProgress = false;
            }
        }

        private void ApplyUpdateUi(UpdateCheckResult result)
        {
            RefreshVersionLabels();

            if (!result.Success)
            {
                SetUpdateStatus(result.Message);
                UpdateBtn.Content = "Check for Updates";
                UpdateBtn.IsEnabled = true;
                InstallUpdateBtn.Visibility = Visibility.Collapsed;
                InstallUpdateBtn.IsEnabled = false;
                return;
            }

            if (result.UpdateAvailable && (result.Staged || _updateService?.IsStaged == true))
            {
                SetUpdateStatus($"v{result.LatestVersion} ready — click Install & Restart.");
                UpdateBtn.Content = "Check for Updates";
                UpdateBtn.IsEnabled = true;
                InstallUpdateBtn.Visibility = Visibility.Visible;
                InstallUpdateBtn.IsEnabled = true;
                return;
            }

            if (result.UpdateAvailable)
            {
                SetUpdateStatus($"v{result.LatestVersion} available.");
                UpdateBtn.Content = "Check for Updates";
                UpdateBtn.IsEnabled = true;
                InstallUpdateBtn.Visibility = Visibility.Collapsed;
                InstallUpdateBtn.IsEnabled = false;
                return;
            }

            SetUpdateStatus(result.Message);
            UpdateBtn.Content = "Check for Updates";
            UpdateBtn.IsEnabled = true;
            InstallUpdateBtn.Visibility = Visibility.Collapsed;
            InstallUpdateBtn.IsEnabled = false;
        }

        private void SetUpdateStatus(string text)
        {
            UpdateStatusText.Text = text;
        }

        // ─────────────────────────────────────────────────
        //  UI sync helpers
        // ─────────────────────────────────────────────────

        private void SyncUI()
        {
            if (_workAssist != null)
            {
                bool on = _workAssist.IsEnabled;
                WorkAssistDot.Fill = on
                    ? (SolidColorBrush)FindResource("AccentGreenBrush")
                    : (SolidColorBrush)FindResource("AccentRedBrush");
                WorkAssistToggle.IsChecked = on;
            }
            if (_sprint != null)
            {
                bool on = _sprint.IsEnabled;
                SprintDot.Fill = on
                    ? (SolidColorBrush)FindResource("AccentGreenBrush")
                    : (SolidColorBrush)FindResource("AccentRedBrush");
                SprintToggle.IsChecked = on;
            }
            SyncSprintStatus();
            UpdateHud();
        }

        private void SyncSprintStatus()
        {
            if (_sprint == null) return;
            var (text, brush) = _sprint.State switch
            {
                SprintState.Sprinting    => ("⚡ Sprinting…",   FindResource("AccentCyanBrush")),
                SprintState.Recovering   => ("🔋 Recovering…", FindResource("AccentYellowBrush")),
                SprintState.DodgePausing => ("🛡 Dodge pause",  FindResource("AccentRedBrush")),
                _                        => ("Idle",            FindResource("TextSecondaryBrush"))
            };
            SprintStatusText.Text = text;
            SprintStatusText.Foreground = (Brush)brush;
        }

        // ─────────────────────────────────────────────────
        //  Consolidated HUD
        // ─────────────────────────────────────────────────

        private void UpdateHud()
        {
            HudStack.Children.Clear();

            bool anyActive = false;

            if (_workAssist != null && _workAssist.IsEnabled && (_configManager == null || _configManager.Config.WorkAssistShowHud))
            {
                anyActive = true;
                AddHudRow("Work Assist", "Active", (SolidColorBrush)FindResource("AccentGreenBrush"));
            }

            if (_sprint != null && _sprint.IsEnabled)
            {
                anyActive = true;
                var (label, brush) = _sprint.State switch
                {
                    SprintState.Sprinting    => ("Sprinting",  (SolidColorBrush)FindResource("AccentCyanBrush")),
                    SprintState.Recovering   => ("Recovering", (SolidColorBrush)FindResource("AccentYellowBrush")),
                    SprintState.DodgePausing => ("Dodge",      (SolidColorBrush)FindResource("AccentRedBrush")),
                    _                        => ("Active",     (SolidColorBrush)FindResource("AccentGreenBrush"))
                };
                AddHudRow("Sprint", label, brush);
            }

            HudPanel.Visibility = anyActive ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddHudRow(string name, string status, SolidColorBrush statusBrush)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            sp.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = statusBrush,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"{name}: {status}",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            HudStack.Children.Add(sp);
        }

        // ─────────────────────────────────────────────────
        //  HUD positioning
        // ─────────────────────────────────────────────────

        private void PositionHud()
        {
            if (_configManager == null) return;
            var cfg = _configManager.Config;

            // If custom drag position exists, use it
            if (!double.IsNaN(cfg.HudX) && !double.IsNaN(cfg.HudY))
            {
                Canvas.SetLeft(HudPanel, cfg.HudX);
                Canvas.SetTop(HudPanel, cfg.HudY);
                return;
            }

            // Preset positions
            HudPanel.UpdateLayout();
            double cw = RootCanvas.ActualWidth > 0  ? RootCanvas.ActualWidth  : this.Width;
            double ch = RootCanvas.ActualHeight > 0 ? RootCanvas.ActualHeight : this.Height;
            double hw = HudPanel.ActualWidth  > 0 ? HudPanel.ActualWidth  : 140;
            double hh = HudPanel.ActualHeight > 0 ? HudPanel.ActualHeight : 30;
            const double pad = 16;

            var (x, y) = cfg.HudPreset switch
            {
                "TopLeft"      => (pad, pad),
                "TopCenter"    => ((cw - hw) / 2, pad),
                "TopRight"     => (cw - hw - pad, pad),
                "BottomLeft"   => (pad, ch - hh - pad),
                "BottomCenter" => ((cw - hw) / 2, ch - hh - pad),
                "BottomRight"  => (cw - hw - pad, ch - hh - pad),
                _              => (cw - hw - pad, pad)
            };

            Canvas.SetLeft(HudPanel, Math.Max(0, x));
            Canvas.SetTop(HudPanel,  Math.Max(0, y));
        }

        // ─────────────────────────────────────────────────
        //  HUD drag
        // ─────────────────────────────────────────────────

        private void HudPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_configManager == null || !_configManager.Config.HudDraggable) return;
            _isHudDragging = true;
            _hudDragStart = e.GetPosition(RootCanvas);
            _hudDragStartLeft = Canvas.GetLeft(HudPanel);
            _hudDragStartTop  = Canvas.GetTop(HudPanel);
            if (double.IsNaN(_hudDragStartLeft)) _hudDragStartLeft = 0;
            if (double.IsNaN(_hudDragStartTop))  _hudDragStartTop  = 0;
            HudPanel.CaptureMouse();
            e.Handled = true;
        }

        private void HudPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isHudDragging) return;
            var pos = e.GetPosition(RootCanvas);
            double x = _hudDragStartLeft + (pos.X - _hudDragStart.X);
            double y = _hudDragStartTop  + (pos.Y - _hudDragStart.Y);
            Canvas.SetLeft(HudPanel, Math.Clamp(x, 0, Math.Max(0, RootCanvas.ActualWidth  - HudPanel.ActualWidth)));
            Canvas.SetTop(HudPanel,  Math.Clamp(y, 0, Math.Max(0, RootCanvas.ActualHeight - HudPanel.ActualHeight)));
        }

        private void HudPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isHudDragging) return;
            _isHudDragging = false;
            HudPanel.ReleaseMouseCapture();
            // Save custom position, overriding preset
            if (_configManager != null)
            {
                _configManager.Config.HudX = Canvas.GetLeft(HudPanel);
                _configManager.Config.HudY = Canvas.GetTop(HudPanel);
                _configManager.Save();
            }
        }

        // ─────────────────────────────────────────────────
        //  Menu card drag
        // ─────────────────────────────────────────────────

        private void CentreMenuIfNeeded()
        {
            double left = Canvas.GetLeft(MenuPanel);
            if (!double.IsNaN(left) && left >= 0) return;
            MenuPanel.UpdateLayout();
            double cx = (RootCanvas.ActualWidth  - MenuPanel.ActualWidth)  / 2;
            double cy = (RootCanvas.ActualHeight - MenuPanel.ActualHeight) / 2;
            Canvas.SetLeft(MenuPanel, Math.Max(0, cx));
            Canvas.SetTop(MenuPanel,  Math.Max(0, cy));
        }

        private void ClampMenuToCanvas()
        {
            double left = Canvas.GetLeft(MenuPanel);
            double top  = Canvas.GetTop(MenuPanel);
            if (double.IsNaN(left) || double.IsNaN(top)) return;
            double maxL = Math.Max(0, RootCanvas.ActualWidth  - MenuPanel.ActualWidth);
            double maxT = Math.Max(0, RootCanvas.ActualHeight - MenuPanel.ActualHeight);
            Canvas.SetLeft(MenuPanel, Math.Clamp(left, 0, maxL));
            Canvas.SetTop(MenuPanel,  Math.Clamp(top,  0, maxT));
        }

        private void MenuPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _isDragging    = true;
            _dragStart     = e.GetPosition(RootCanvas);
            _dragStartLeft = Canvas.GetLeft(MenuPanel);
            _dragStartTop  = Canvas.GetTop(MenuPanel);
            MenuPanel.CaptureMouse();
            e.Handled = true;
        }

        private void MenuPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var pos = e.GetPosition(RootCanvas);
            double newL = _dragStartLeft + (pos.X - _dragStart.X);
            double newT = _dragStartTop  + (pos.Y - _dragStart.Y);
            double maxL = Math.Max(0, RootCanvas.ActualWidth  - MenuPanel.ActualWidth);
            double maxT = Math.Max(0, RootCanvas.ActualHeight - MenuPanel.ActualHeight);
            Canvas.SetLeft(MenuPanel, Math.Clamp(newL, 0, maxL));
            Canvas.SetTop(MenuPanel,  Math.Clamp(newT, 0, maxT));
        }

        private void MenuPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            MenuPanel.ReleaseMouseCapture();

            if (_configManager != null)
            {
                _configManager.Config.MenuX = Canvas.GetLeft(MenuPanel);
                _configManager.Config.MenuY = Canvas.GetTop(MenuPanel);
                _configManager.Save();
            }
        }

        // ─────────────────────────────────────────────────
        //  Cleanup
        // ─────────────────────────────────────────────────

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Minimize to tray instead of exiting (unless Exit was chosen from tray)
            if (!_allowClose
                && _configManager?.Config.MinimizeToTray == true
                && _configManager.Config.TrayEnabled)
            {
                e.Cancel = true;
                SetMenuVisible(false);
                ShowInTaskbar = false;
                _trayService?.ShowBalloon("PalAssist 2", "Still running in the tray. Right-click the icon to Exit.", 2500);
                return;
            }

            // Real exit: always release keys first (before any dispose that might throw)
            try
            {
                _featureManager?.ReleaseAllInput();
            }
            catch
            {
                FeatureManager.ForceReleaseCommonKeys();
            }

            EmergencyRelease.SetCallback(null);

            _uiTimer?.Stop();
            _focusResumeTimer?.Stop();
            _updateCts?.Cancel();
            SetMenuCursorForced(false);

            if (_configManager != null)
            {
                try
                {
                    if (_workAssist != null)  _configManager.Config.WorkAssistEnabled  = _workAssist.IsEnabled;
                    _configManager.Config.SprintEnabled = SprintAssistAvailable && _sprint != null && _sprint.IsEnabled;
                    _configManager.Config.BetaProfileWorkEnabled = false;
                    _configManager.Config.MenuX = Canvas.GetLeft(MenuPanel);
                    _configManager.Config.MenuY = Canvas.GetTop(MenuPanel);
                    _configManager.Config.BetaEnabled = BetaEnabledToggle.IsChecked == true;
                    _configManager.Config.FocusLockEnabled = FocusLockToggle.IsChecked == true;
                    _configManager.Config.BetaSmartWorkAssist = BetaSmartWorkAssistToggle.IsChecked == true
                                                               && _configManager.Config.BetaEnabled;
                    _configManager.Config.AfkSafetyEnabled = AfkSafetyToggle.IsChecked == true;
                    SyncAppearanceConfigFromUi(_configManager.Config);
                    SyncSoundConfigFromUi(_configManager.Config);
                    SyncTrayConfigFromUi(_configManager.Config);
                    SyncCrosshairConfigFromUi(_configManager.Config);
                    _configManager.Save();
                }
                catch
                {
                    // never block exit on save failure
                }
            }

            try { _trayService?.Dispose(); } catch { /* ignore */ }
            try { _featureManager?.Dispose(); } catch { FeatureManager.ForceReleaseCommonKeys(); }
            try { _hotkeyManager?.Dispose(); } catch { /* ignore */ }
            try { _windowTracker?.Dispose(); } catch { /* ignore */ }
            try { _updateService?.Dispose(); } catch { /* ignore */ }
        }

        private void RequestExit()
        {
            _allowClose = true;
            try { _featureManager?.ReleaseAllInput(); } catch { FeatureManager.ForceReleaseCommonKeys(); }
            Close();
        }

        // ─────────────────────────────────────────────────
        //  Config Manager
        // ─────────────────────────────────────────────────

        private void ConfigOpenFolderBtn_Click(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            try
            {
                string dir = _configManager.ConfigDirectory;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
                ConfigStatusText.Text = "Opened config folder.";
            }
            catch (Exception ex)
            {
                ConfigStatusText.Text = "Could not open folder: " + ex.Message;
            }
        }

        private void ConfigExportBtn_Click(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            SyncLiveConfigToManager();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export PalAssist config",
                Filter = "JSON config|*.json|All files|*.*",
                FileName = "PalAssist-config.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dlg.ShowDialog() != true) return;
            bool ok = _configManager.ExportTo(dlg.FileName);
            ConfigStatusText.Text = ok ? "Config exported." : "Export failed.";
        }

        private void ConfigImportBtn_Click(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import PalAssist config",
                Filter = "JSON config|*.json|All files|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            var confirm = MessageBox.Show(
                this,
                "Import this config and replace your current settings?\n\nHotkeys and appearance will re-apply immediately.",
                "Import config",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            bool ok = _configManager.ImportFrom(dlg.FileName);
            if (!ok)
            {
                ConfigStatusText.Text = "Import failed — invalid or unreadable file.";
                return;
            }

            ApplyImportedConfigToUi();
            ConfigStatusText.Text = "Config imported.";
        }

        private void ConfigResetBtn_Click(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            var confirm = MessageBox.Show(
                this,
                "Reset all settings to defaults?\n\nThis cannot be undone (export a backup first if needed).",
                "Reset defaults",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            // Keep last_seen_version so What's New doesn't spam after reset
            string lastSeen = _configManager.Config.LastSeenVersion;
            bool ok = _configManager.ResetToDefaults();
            if (!ok)
            {
                ConfigStatusText.Text = "Reset failed.";
                return;
            }
            _configManager.Config.LastSeenVersion = lastSeen;
            _configManager.Save();
            ApplyImportedConfigToUi();
            ConfigStatusText.Text = "Settings reset to defaults.";
        }

        /// <summary>Push current UI feature state into ConfigManager before export/save.</summary>
        private void SyncLiveConfigToManager()
        {
            if (_configManager == null) return;
            var cfg = _configManager.Config;
            if (_workAssist != null) cfg.WorkAssistEnabled = _workAssist.IsEnabled;
            cfg.SprintEnabled = SprintAssistAvailable && _sprint != null && _sprint.IsEnabled;
            cfg.WorkAssistShowHud = WorkAssistShowHudCheck.IsChecked == true;
            cfg.FocusLockEnabled = FocusLockToggle.IsChecked == true;
            cfg.BetaEnabled = BetaEnabledToggle.IsChecked == true;
            cfg.BetaSmartWorkAssist = BetaSmartWorkAssistToggle.IsChecked == true && cfg.BetaEnabled;
            cfg.AfkSafetyEnabled = AfkSafetyToggle.IsChecked == true;
            cfg.MenuX = Canvas.GetLeft(MenuPanel);
            cfg.MenuY = Canvas.GetTop(MenuPanel);
            SyncAppearanceConfigFromUi(cfg);
            SyncSoundConfigFromUi(cfg);
            SyncTrayConfigFromUi(cfg);
            SyncCrosshairConfigFromUi(cfg);
            if (HudPresetCombo.SelectedItem != null)
                cfg.HudPreset = ((ComboBoxItem)HudPresetCombo.SelectedItem).Content.ToString()!.Replace("-", "");
            _configManager.Save();
        }

        /// <summary>Re-apply config after import/reset (hotkeys, toggles, appearance).</summary>
        private void ApplyImportedConfigToUi()
        {
            if (_configManager == null) return;
            var cfg = _configManager.Config;

            // Stop assists before rebinding
            StopAllAssists(playSound: false, notify: false);

            ThemeService.Apply(cfg.UiTheme);
            ApplySoundFromConfig(cfg);
            RestoreAppearanceFromConfig(cfg);
            RestoreSoundUiFromConfig(cfg);
            RestoreTrayUiFromConfig(cfg);
            RestoreCrosshairFromConfig(cfg);
            RestoreFocusLockFromConfig(cfg);
            SetAfkSafetyToggleSilent(cfg.AfkSafetyEnabled);

            ApplyBetaTabVisibility(cfg.BetaEnabled);
            SetBetaEnabledToggleSilent(cfg.BetaEnabled);
            if (cfg.BetaEnabled)
                RestoreBetaUiFromConfig(cfg);
            else
                ApplySmartWorkAssistToFeature(false);

            HudDraggableToggle.IsChecked = cfg.HudDraggable;
            WorkAssistShowHudCheck.IsChecked = cfg.WorkAssistShowHud;
            SetHudPresetCombo(cfg.HudPreset);
            SprintDurSlider.Value = cfg.SprintDuration;
            RecoveryDurSlider.Value = cfg.RecoveryDuration;
            PauseDodgeCheck.IsChecked = cfg.SprintPauseDodge;

            // Re-register hotkeys from imported names
            try
            {
                if (_hotkeyManager != null)
                {
                    if (_menuHotkeyId >= 0) _hotkeyManager.Unregister(_menuHotkeyId);
                    if (_workAssistHotkeyId >= 0) _hotkeyManager.Unregister(_workAssistHotkeyId);
                    if (_sprintHotkeyId >= 0) _hotkeyManager.Unregister(_sprintHotkeyId);
                    _menuHotkeyId = _workAssistHotkeyId = _sprintHotkeyId = -1;
                    RegisterConfigHotkeys();
                }
            }
            catch
            {
                ConfigStatusText.Text = "Config loaded — restart recommended if hotkeys misbehave.";
            }

            InitTray(cfg);
            PositionHud();
            SyncUI();
        }

        // ─────────────────────────────────────────────────
        //  Appearance / Sound / Tray / Changelog
        // ─────────────────────────────────────────────────

        private void RestoreAppearanceFromConfig(AppConfig cfg)
        {
            _suppressAppearanceHandlers = true;
            try
            {
                double opacity = Math.Clamp(cfg.UiOpacity, 0.5, 1.0);
                UiOpacitySlider.Value = opacity;
                UiOpacityLabel.Text = $"{(int)Math.Round(opacity * 100)}%";
                SelectScaleCombo(cfg.UiScale);
                SelectComboByContent(UiThemeCombo, ThemeService.Normalize(cfg.UiTheme), "Cyan");
            }
            finally
            {
                _suppressAppearanceHandlers = false;
            }
            ApplyAppearance(cfg.UiOpacity, cfg.UiScale, cfg.UiTheme, save: false);
        }

        private void SelectScaleCombo(double scale)
        {
            ComboBoxItem? best = null;
            double bestDiff = double.MaxValue;
            foreach (ComboBoxItem item in UiScaleCombo.Items)
            {
                if (item.Tag is string t && double.TryParse(t, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                {
                    double d = Math.Abs(v - scale);
                    if (d < bestDiff)
                    {
                        bestDiff = d;
                        best = item;
                    }
                }
            }
            if (best != null)
                UiScaleCombo.SelectedItem = best;
        }

        private void ApplyAppearance(double opacity, double scale, string theme, bool save)
        {
            opacity = Math.Clamp(opacity, 0.5, 1.0);
            scale = Math.Clamp(scale, 0.75, 1.5);
            theme = ThemeService.Normalize(theme);

            ThemeService.Apply(theme);
            MenuPanel.Opacity = opacity;
            HudPanel.Opacity = opacity;

            if (MenuPanel.LayoutTransform is not ScaleTransform st)
            {
                st = new ScaleTransform(scale, scale);
                MenuPanel.LayoutTransform = st;
            }
            else
            {
                st.ScaleX = scale;
                st.ScaleY = scale;
            }

            if (HudPanel.LayoutTransform is not ScaleTransform hst)
            {
                hst = new ScaleTransform(scale, scale);
                HudPanel.LayoutTransform = hst;
            }
            else
            {
                hst.ScaleX = scale;
                hst.ScaleY = scale;
            }

            ClampMenuToCanvas();
            PositionHud();
            UpdateFocusLockStatus();
            if (_featureManager != null)
                SyncUI();

            if (save && _configManager != null)
            {
                _configManager.Config.UiOpacity = opacity;
                _configManager.Config.UiScale = scale;
                _configManager.Config.UiTheme = theme;
                _configManager.Save();
            }
        }

        private void SyncAppearanceConfigFromUi(AppConfig cfg)
        {
            cfg.UiOpacity = Math.Clamp(UiOpacitySlider.Value, 0.5, 1.0);
            cfg.UiScale = 1.0;
            if (UiScaleCombo.SelectedItem is ComboBoxItem si && si.Tag is string t
                && double.TryParse(t, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double scale))
                cfg.UiScale = scale;
            cfg.UiTheme = ThemeService.Normalize(
                (UiThemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString());
        }

        private void UiOpacitySlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressAppearanceHandlers || _configManager == null) return;
            UiOpacityLabel.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
            SyncAppearanceConfigFromUi(_configManager.Config);
            ApplyAppearance(_configManager.Config.UiOpacity, _configManager.Config.UiScale,
                _configManager.Config.UiTheme, save: true);
        }

        private void UiScaleCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (_suppressAppearanceHandlers || _configManager == null) return;
            SyncAppearanceConfigFromUi(_configManager.Config);
            ApplyAppearance(_configManager.Config.UiOpacity, _configManager.Config.UiScale,
                _configManager.Config.UiTheme, save: true);
        }

        private void UiThemeCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (_suppressAppearanceHandlers || _configManager == null) return;
            SyncAppearanceConfigFromUi(_configManager.Config);
            ApplyAppearance(_configManager.Config.UiOpacity, _configManager.Config.UiScale,
                _configManager.Config.UiTheme, save: true);
        }

        private void ApplySoundFromConfig(AppConfig cfg)
        {
            _soundService.Enabled = cfg.SoundEnabled;
            _soundService.OnToggle = cfg.SoundOnToggle;
            _soundService.OnFocus = cfg.SoundOnFocus;
            _soundService.OnUpdate = cfg.SoundOnUpdate;
        }

        private void RestoreSoundUiFromConfig(AppConfig cfg)
        {
            _suppressSoundHandlers = true;
            try
            {
                SoundEnabledToggle.IsChecked = cfg.SoundEnabled;
                SoundOnToggleCheck.IsChecked = cfg.SoundOnToggle;
                SoundOnFocusCheck.IsChecked = cfg.SoundOnFocus;
                SoundOnUpdateCheck.IsChecked = cfg.SoundOnUpdate;
            }
            finally
            {
                _suppressSoundHandlers = false;
            }
            ApplySoundFromConfig(cfg);
        }

        private void SyncSoundConfigFromUi(AppConfig cfg)
        {
            cfg.SoundEnabled = SoundEnabledToggle.IsChecked == true;
            cfg.SoundOnToggle = SoundOnToggleCheck.IsChecked == true;
            cfg.SoundOnFocus = SoundOnFocusCheck.IsChecked == true;
            cfg.SoundOnUpdate = SoundOnUpdateCheck.IsChecked == true;
        }

        private void SoundSettings_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressSoundHandlers || _configManager == null) return;
            SyncSoundConfigFromUi(_configManager.Config);
            ApplySoundFromConfig(_configManager.Config);
            _configManager.Save();
        }

        private void RestoreTrayUiFromConfig(AppConfig cfg)
        {
            _suppressTrayHandlers = true;
            try
            {
                TrayEnabledToggle.IsChecked = cfg.TrayEnabled;
                MinimizeToTrayToggle.IsChecked = cfg.MinimizeToTray;
            }
            finally
            {
                _suppressTrayHandlers = false;
            }
        }

        private void SyncTrayConfigFromUi(AppConfig cfg)
        {
            cfg.TrayEnabled = TrayEnabledToggle.IsChecked == true;
            cfg.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        }

        private void TraySettings_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressTrayHandlers || _configManager == null) return;
            SyncTrayConfigFromUi(_configManager.Config);
            _configManager.Save();

            if (_configManager.Config.TrayEnabled)
            {
                if (_trayService == null)
                    InitTray(_configManager.Config);
                else
                    _trayService.SetVisible(true);
            }
            else
            {
                _trayService?.SetVisible(false);
                ShowInTaskbar = true;
            }
        }

        private void InitTray(AppConfig cfg)
        {
            if (!cfg.TrayEnabled) return;

            if (_trayService == null)
            {
                _trayService = new TrayService();
                _trayService.Initialize($"PalAssist 2 v{UpdateService.GetCurrentVersion()}");
                _trayService.ShowMenuRequested += () => UiPost(() =>
                {
                    ShowInTaskbar = true;
                    SetMenuVisible(true);
                    Activate();
                });
                _trayService.HideMenuRequested += () => UiPost(() => SetMenuVisible(false));
                _trayService.ExitRequested += () => UiPost(RequestExit);
            }
            else
            {
                _trayService.SetTooltip($"PalAssist 2 v{UpdateService.GetCurrentVersion()}");
            }

            _trayService.SetVisible(true);
        }

        private void MaybeShowWhatsNew(AppConfig cfg)
        {
            string current = UpdateService.GetCurrentVersion();
            string last = cfg.LastSeenVersion?.Trim() ?? "";

            // Brand-new install: record version, skip popup
            if (string.IsNullOrEmpty(last) && _configManager is { ConfigFileExistedOnLoad: false })
            {
                cfg.LastSeenVersion = current;
                _configManager.Save();
                return;
            }

            // Upgrade from a build that had no last_seen_version: show this version's notes once
            if (string.IsNullOrEmpty(last))
            {
                string notes = ChangelogService.GetNotesForVersion(current);
                if (string.IsNullOrWhiteSpace(notes))
                    notes = ChangelogService.GetNotesSince("0.0.0", current);
                if (!string.IsNullOrWhiteSpace(notes))
                    ShowWhatsNewDialog($"What's New in v{current}", notes);
                cfg.LastSeenVersion = current;
                _configManager?.Save();
                return;
            }

            if (!UpdateService.TryParseVersion(current, out var curVer)
                || !UpdateService.TryParseVersion(last, out var lastVer)
                || curVer <= lastVer)
            {
                return;
            }

            string sinceNotes = ChangelogService.GetNotesSince(last, current);
            ShowWhatsNewDialog($"What's New in v{current}", sinceNotes);

            cfg.LastSeenVersion = current;
            _configManager?.Save();
        }

        private void ChangelogBtn_Click(object s, RoutedEventArgs e)
        {
            BuildChangelogPanel();
            SwitchTab("changelog");
        }

        /// <summary>
        /// Fills the What's Changed tab with version cards (newest first).
        /// </summary>
        private void BuildChangelogPanel()
        {
            ChangelogStack.Children.Clear();
            var entries = ChangelogService.GetAllEntries();
            string current = UpdateService.GetCurrentVersion();
            var cyan = (SolidColorBrush)FindResource("AccentCyanBrush");
            var primary = (SolidColorBrush)FindResource("TextPrimaryBrush");
            var secondary = (SolidColorBrush)FindResource("TextSecondaryBrush");

            if (entries.Count == 0)
            {
                ChangelogStack.Children.Add(new TextBlock
                {
                    Text = "No changelog entries found.",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = secondary,
                    Margin = new Thickness(14, 8, 14, 8),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var entry in entries)
            {
                bool isCurrent = string.Equals(entry.Version, current, StringComparison.OrdinalIgnoreCase);
                var border = new Border
                {
                    Margin = new Thickness(14, 2, 14, 6),
                    Padding = new Thickness(14, 10, 14, 10),
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Color.FromArgb(0x99, 0x1A, 0x1A, 0x2E)),
                    BorderBrush = isCurrent ? cyan : Brushes.Transparent,
                    BorderThickness = isCurrent ? new Thickness(1.5) : new Thickness(0)
                };

                var stack = new StackPanel();
                string title = isCurrent ? $"v{entry.Version}  ·  current" : $"v{entry.Version}";
                stack.Children.Add(new TextBlock
                {
                    Text = title,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isCurrent ? cyan : primary,
                    Margin = new Thickness(0, 0, 0, 6)
                });

                string body = string.IsNullOrWhiteSpace(entry.Body)
                    ? "(No notes for this version.)"
                    : entry.Body.Trim();
                stack.Children.Add(new TextBlock
                {
                    Text = body,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = secondary,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18
                });

                border.Child = stack;
                ChangelogStack.Children.Add(border);
            }
        }

        private void ShowWhatsNewDialog(string title, string body)
        {
            var dlg = new Window
            {
                Title = title,
                Owner = this,
                Width = 420,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Background = (SolidColorBrush)FindResource("BgCardBrush"),
                ShowInTaskbar = false
            };

            var root = new DockPanel { Margin = new Thickness(16) };
            var ok = new Button
            {
                Content = "OK",
                Width = 88,
                Height = 30,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("RebindBtnStyle")
            };
            ok.Click += (_, _) => dlg.Close();
            DockPanel.SetDock(ok, Dock.Bottom);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(body) ? "No notes for this version." : body,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush")
                }
            };

            root.Children.Add(ok);
            root.Children.Add(scroll);
            dlg.Content = root;
            dlg.ShowDialog();
        }

        // ─────────────────────────────────────────────────
        //  Crosshair
        // ─────────────────────────────────────────────────

        private void RestoreCrosshairFromConfig(AppConfig cfg)
        {
            _suppressCrosshairHandlers = true;
            try
            {
                SelectComboByContent(CrosshairStyleCombo, cfg.CrosshairStyle, "Dot");
                SelectComboByContent(CrosshairSizeCombo, cfg.CrosshairSize, "Medium");
                SelectComboByContent(CrosshairColorCombo, cfg.CrosshairColor, "White");
                CrosshairToggle.IsChecked = cfg.CrosshairEnabled;
            }
            finally
            {
                _suppressCrosshairHandlers = false;
            }

            ApplyCrosshairState(cfg.CrosshairEnabled);
        }

        private static void SelectComboByContent(ComboBox combo, string value, string fallback)
        {
            ComboBoxItem? match = null;
            ComboBoxItem? fallbackItem = null;
            foreach (ComboBoxItem item in combo.Items)
            {
                string content = item.Content?.ToString() ?? "";
                if (string.Equals(content, value, StringComparison.OrdinalIgnoreCase))
                    match = item;
                if (string.Equals(content, fallback, StringComparison.OrdinalIgnoreCase))
                    fallbackItem = item;
            }
            combo.SelectedItem = match ?? fallbackItem ?? (combo.Items.Count > 0 ? combo.Items[0] : null);
        }

        private void SyncCrosshairConfigFromUi(AppConfig cfg)
        {
            cfg.CrosshairEnabled = CrosshairToggle.IsChecked == true;
            cfg.CrosshairStyle = (CrosshairStyleCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dot";
            cfg.CrosshairSize = (CrosshairSizeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Medium";
            cfg.CrosshairColor = (CrosshairColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "White";
        }

        private void SaveCrosshairConfig()
        {
            if (_configManager == null) return;
            SyncCrosshairConfigFromUi(_configManager.Config);
            _configManager.Save();
        }

        private void CrosshairToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressCrosshairHandlers || _configManager == null) return;
            bool on = CrosshairToggle.IsChecked == true;
            ApplyCrosshairState(on);
            SaveCrosshairConfig();
        }

        private void CrosshairOption_Changed(object s, SelectionChangedEventArgs e)
        {
            if (_suppressCrosshairHandlers || _configManager == null) return;
            if (CrosshairToggle.IsChecked == true)
                UpdateCrosshairVisual();
            SaveCrosshairConfig();
        }

        private void ApplyCrosshairState(bool enabled)
        {
            CrosshairOptionsPanel.IsEnabled = enabled;
            if (enabled)
            {
                UpdateCrosshairVisual();
                PositionCrosshair();
                CrosshairCanvas.Visibility = Visibility.Visible;
            }
            else
            {
                CrosshairCanvas.Visibility = Visibility.Collapsed;
                CrosshairCanvas.Children.Clear();
            }
        }

        private void PositionCrosshair()
        {
            double size = CrosshairCanvas.Width;
            if (size <= 0) size = 48;
            Canvas.SetLeft(CrosshairCanvas, (ActualWidth - size) / 2.0);
            Canvas.SetTop(CrosshairCanvas, (ActualHeight - size) / 2.0);
        }

        private void UpdateCrosshairVisual()
        {
            string style = (CrosshairStyleCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dot";
            string sizeName = (CrosshairSizeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Medium";
            string colorName = (CrosshairColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "White";

            double half = sizeName.ToLowerInvariant() switch
            {
                "small" => 6,
                "large" => 20,
                _ => 12
            };
            double thickness = sizeName.ToLowerInvariant() switch
            {
                "small" => 1.5,
                "large" => 3,
                _ => 2
            };
            double canvasSize = half * 2 + 4;
            CrosshairCanvas.Width = canvasSize;
            CrosshairCanvas.Height = canvasSize;

            Brush brush = colorName.ToLowerInvariant() switch
            {
                "cyan" => (SolidColorBrush)FindResource("AccentCyanBrush"),
                "red" => (SolidColorBrush)FindResource("AccentRedBrush"),
                "green" => (SolidColorBrush)FindResource("AccentGreenBrush"),
                "yellow" => (SolidColorBrush)FindResource("AccentYellowBrush"),
                "magenta" => new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF)),
                "orange" => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
                "blue" => new SolidColorBrush(Color.FromRgb(0x29, 0x79, 0xFF)),
                "pink" => new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0xAB)),
                "black" => new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
                "lime" => new SolidColorBrush(Color.FromRgb(0xC6, 0xFF, 0x00)),
                _ => Brushes.White
            };

            CrosshairCanvas.Children.Clear();
            double center = canvasSize / 2.0;

            if (string.Equals(style, "Dot", StringComparison.OrdinalIgnoreCase))
            {
                double diameter = half;
                var dot = new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = brush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, center - diameter / 2.0);
                Canvas.SetTop(dot, center - diameter / 2.0);
                CrosshairCanvas.Children.Add(dot);
            }
            else if (string.Equals(style, "Plus", StringComparison.OrdinalIgnoreCase))
            {
                CrosshairCanvas.Children.Add(MakeCrosshairLine(
                    center - half, center, center + half, center, brush, thickness));
                CrosshairCanvas.Children.Add(MakeCrosshairLine(
                    center, center - half, center, center + half, brush, thickness));
            }
            else // X
            {
                CrosshairCanvas.Children.Add(MakeCrosshairLine(
                    center - half, center - half, center + half, center + half, brush, thickness));
                CrosshairCanvas.Children.Add(MakeCrosshairLine(
                    center - half, center + half, center + half, center - half, brush, thickness));
            }

            PositionCrosshair();
        }

        private static Line MakeCrosshairLine(
            double x1, double y1, double x2, double y2, Brush brush, double thickness)
        {
            return new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
        }
    }
}
