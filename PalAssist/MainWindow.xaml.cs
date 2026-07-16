using System;
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
        private CancellationTokenSource? _updateCts;
        private bool _updateInProgress;

        // ── Features ──
        private WorkAssistFeature?         _workAssist;
        private SprintAssistFeature?  _sprint;
        private ProfileWorkFeature?   _profileWork;

        // ── State ──
        private bool   _menuVisible = false;
        private IntPtr _hwnd;

        // ── Hotkey IDs ──
        private int _menuHotkeyId   = -1;
        private int _workAssistHotkeyId  = -1;
        private int _sprintHotkeyId = -1;

        // ── Rebind state ──
        private string? _rebindTarget = null;

        // ── Setup wizard ──
        private int _wizardStep = -1; // -1 = inactive, 0..N = step index
        private static readonly string[] WizardTargets = SprintAssistAvailable
            ? new[] { "menu", "workAssist", "sprint" }
            : new[] { "menu", "workAssist" };
        private static readonly string[] WizardPrompts = SprintAssistAvailable
            ? new[]
            {
                "Press a key for Menu toggle…",
                "Press a key for Work Assist…",
                "Press a key for Sprint Assist…"
            }
            : new[]
            {
                "Press a key for Menu toggle…",
                "Press a key for Work Assist…"
            };

        // ── Menu cursor force (ShowCursor ref-count balance) ──
        private int _menuCursorForceCount;
        private bool _menuCursorForced;

        // ── Session timer ──
        private DispatcherTimer? _sessionTimer;
        private DateTime _sessionStartedUtc;
        private DateTime _sessionLastRemindUtc;
        private bool _sessionReminderActive;

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
        private string _activeTab = "assists";

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
                _sprint.StateChanged += () => Dispatcher.Invoke(SyncSprintStatus);
                _featureManager.Register(_sprint);
            }
            else
            {
                // Ensure a previous install cannot leave sprint "on" in config
                cfg.SprintEnabled = false;
            }

            _profileWork = new ProfileWorkFeature();
            ApplyProfileFromConfig(cfg);
            _featureManager.Register(_profileWork);

            _featureManager.StateChanged += () => Dispatcher.Invoke(SyncUI);
            _featureManager.Start();

            // ── Window tracker ──
            _windowTracker = new WindowTracker();
            _windowTracker.BoundsChanged += rect => Dispatcher.Invoke(() => AlignToGame(rect));
            _windowTracker.GameDetected  += found => Dispatcher.Invoke(() => OnGameDetected(found));
            _windowTracker.FocusChanged  += focused => Dispatcher.Invoke(() => OnGameFocusChanged(focused));
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

            // ── UI timer for sprint status refresh (4 Hz is enough) ──
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _uiTimer.Tick += (_, _) => SyncSprintStatus();
            _uiTimer.Start();

            // ── Beta gate + UI restore (only apply beta features if unlocked) ──
            ApplyBetaTabVisibility(cfg.BetaEnabled);
            SetBetaEnabledToggleSilent(cfg.BetaEnabled);
            if (cfg.BetaEnabled)
                RestoreBetaUiFromConfig(cfg);

            // ── Crosshair restore ──
            RestoreCrosshairFromConfig(cfg);

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

            // ── Session timer ──
            StartSessionTimer();

            // ── Updates ──
            _updateService = new UpdateService();
            RefreshVersionLabels();
            UpdateStatusText.Text = "";
            InstallUpdateBtn.Visibility = Visibility.Collapsed;
            InstallUpdateBtn.IsEnabled = false;

            // Always auto-check on boot; prompt before download/install
            if (cfg.AutoCheckUpdates)
            {
                _ = BootUpdateCheckAsync();
            }

            // Fresh install: only open Beta wizard if Beta is already unlocked
            if (cfg.BetaEnabled
                && !cfg.BetaSetupWizardCompleted
                && _configManager is { ConfigFileExistedOnLoad: false })
            {
                SwitchTab("beta");
                StartSetupWizard();
            }
        }

        /// <summary>
        /// Keep header, About, and taskbar title on the same assembly version.
        /// </summary>
        private void RefreshVersionLabels()
        {
            string v = UpdateService.GetCurrentVersion();
            HeaderVersionText.Text = $"v{v} — External Input Assist";
            VersionText.Text = $"PalAssist v{v}";
            Title = $"PalAssist v{v}";
        }

        // ─────────────────────────────────────────────────
        //  Tab switching
        // ─────────────────────────────────────────────────

        private void TabAssists_Click(object s, RoutedEventArgs e) => SwitchTab("assists");
        private void TabAI_Click(object s, RoutedEventArgs e)      => SwitchTab("ai");
        private void TabBeta_Click(object s, RoutedEventArgs e)    => SwitchTab("beta");
        private void TabSettings_Click(object s, RoutedEventArgs e) => SwitchTab("settings");

        private void SwitchTab(string tab)
        {
            // Beta tab requires opt-in in Settings
            if (tab == "beta" && _configManager?.Config.BetaEnabled != true)
            {
                tab = "assists";
            }

            _activeTab = tab;

            PanelAssists.Visibility  = tab == "assists"  ? Visibility.Visible : Visibility.Collapsed;
            PanelAI.Visibility       = tab == "ai"       ? Visibility.Visible : Visibility.Collapsed;
            PanelBeta.Visibility     = tab == "beta"     ? Visibility.Visible : Visibility.Collapsed;
            PanelSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;

            // Style active tab with cyan underline
            var cyanBrush = (SolidColorBrush)FindResource("AccentCyanBrush");
            var transBrush = Brushes.Transparent;
            var cyanFg = cyanBrush;
            var secFg  = (SolidColorBrush)FindResource("TextSecondaryBrush");

            TabAssists.BorderBrush  = tab == "assists"  ? cyanBrush : transBrush;
            TabAssists.Foreground   = tab == "assists"  ? cyanFg    : secFg;
            TabAI.BorderBrush       = tab == "ai"       ? cyanBrush : transBrush;
            TabAI.Foreground        = tab == "ai"       ? cyanFg    : secFg;
            TabBeta.BorderBrush     = tab == "beta"     ? cyanBrush : transBrush;
            TabBeta.Foreground      = tab == "beta"     ? cyanFg    : secFg;
            TabSettings.BorderBrush = tab == "settings" ? cyanBrush : transBrush;
            TabSettings.Foreground  = tab == "settings" ? cyanFg    : secFg;
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

            WorkAssistSubtitle.Text   = $"Continuously holds the F key  ·  Hotkey: {cfg.HotkeyWorkAssist}";
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
        }

        private void OnSprintHotkeyPressed()
        {
            if (!SprintAssistAvailable || _sprint == null || _featureManager == null) return;
            _featureManager.Toggle(_sprint);
            SprintToggle.IsChecked = _sprint.IsEnabled;
        }

        // ─────────────────────────────────────────────────
        //  Rebind flow
        // ─────────────────────────────────────────────────

        private void RebindMenuBtn_Click(object s, RoutedEventArgs e)   => StartRebind("menu");
        private void RebindWorkAssistBtn_Click(object s, RoutedEventArgs e)  => StartRebind("workAssist");
        private void RebindSprintBtn_Click(object s, RoutedEventArgs e) => StartRebind("sprint");

        private void StartRebind(string target)
        {
            _rebindTarget = target;

            if (target.StartsWith("customKey", StringComparison.Ordinal))
            {
                var btn = target switch
                {
                    "customKey1" => BetaCustomKey1Btn,
                    "customKey2" => BetaCustomKey2Btn,
                    _ => BetaCustomKey3Btn
                };
                btn.Content = "…";
            }
            else if (_wizardStep < 0)
            {
                var btn = target switch { "menu" => RebindMenuBtn, "workAssist" => RebindWorkAssistBtn, _ => RebindSprintBtn };
                btn.Content = "Press a key…";
            }

            BeginKeyCapture();
        }

        private void BeginKeyCapture()
        {
            // Temporarily allow the overlay to be activated/focused so PreviewKeyDown fires.
            // Without this, WS_EX_NOACTIVATE silently blocks all keyboard input.
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

            this.Activate();
            this.Focus();
        }

        /// <summary>Restores WS_EX_NOACTIVATE after a rebind completes or is cancelled.</summary>
        private void EndRebind()
        {
            _rebindTarget = null;

            // Restore the no-activate flag so the game keeps focus normally.
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

            RefreshHotkeyLabels();
            RefreshCustomKeyButtons();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_rebindTarget == null) return;
            Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
            if (key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftCtrl  || key == Key.RightCtrl  ||
                key == Key.LeftAlt   || key == Key.RightAlt   ||
                key == Key.LWin      || key == Key.RWin) return;

            e.Handled = true;

            // Escape → cancel
            if (key == Key.Escape)
            {
                if (_wizardStep >= 0)
                    CancelSetupWizard();
                else
                    EndRebind();
                return;
            }

            uint vk = KeyHelper.WpfKeyToVk(key);
            string name = KeyHelper.WpfKeyToName(key);

            // Unknown key → cancel
            if (vk == 0)
            {
                if (_wizardStep >= 0)
                    CancelSetupWizard();
                else
                    EndRebind();
                return;
            }

            // Save target before EndRebind clears _rebindTarget
            string savedTarget = _rebindTarget!;

            // Custom profile key pick (Beta)
            if (savedTarget.StartsWith("customKey", StringComparison.Ordinal))
            {
                EndRebind();
                ApplyCustomKeyPick(savedTarget, name);
                return;
            }

            ref int id = ref _menuHotkeyId;
            if (savedTarget == "workAssist")  id = ref _workAssistHotkeyId;
            if (savedTarget == "sprint") id = ref _sprintHotkeyId;

            // Restore WS_EX_NOACTIVATE BEFORE re-registering so hotkey fires correctly
            EndRebind();
            ApplyRebind(ref id, vk, name, savedTarget);

            if (_wizardStep >= 0)
                AdvanceSetupWizard();
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
                return;
            }

            string? platform = _windowTracker?.Platform;
            GameStatusText.Text = string.IsNullOrEmpty(platform)
                ? "Palworld connected"
                : $"Palworld connected ({platform})";
        }

        // ─────────────────────────────────────────────────
        //  Feature UI event handlers
        // ─────────────────────────────────────────────────

        private void WorkAssistToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_workAssist == null || _featureManager == null) return;
            bool want = WorkAssistToggle.IsChecked == true;
            if (want != _workAssist.IsEnabled) _featureManager.Toggle(_workAssist);
        }

        private void SprintToggle_Changed(object s, RoutedEventArgs e)
        {
            if (!SprintAssistAvailable || _sprint == null || _featureManager == null) return;
            bool want = SprintToggle.IsChecked == true;
            if (want != _sprint.IsEnabled) _featureManager.Toggle(_sprint);
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
        /// </summary>
        private void DisableBetaFeatures()
        {
            if (_featureManager != null && _profileWork != null && _profileWork.IsEnabled)
                _featureManager.Toggle(_profileWork);

            if (_featureManager != null)
                _featureManager.SetInputSuspended(false);

            if (_configManager != null)
            {
                _configManager.Config.BetaFocusLock = false;
                _configManager.Config.BetaProfileWorkEnabled = false;
            }

            BetaFocusLockToggle.IsChecked = false;
            BetaProfileWorkToggle.IsChecked = false;
            UpdateFocusLockStatus();
            UpdateProfileKeysLabel();
            UpdateHud();
        }

        private void RestoreBetaUiFromConfig(AppConfig cfg)
        {
            BetaFocusLockToggle.IsChecked = cfg.BetaFocusLock;
            BetaSessionAutoStopCheck.IsChecked = cfg.BetaSessionAutoStopAssists;
            SetBetaSessionRemindCombo(cfg.BetaSessionRemindMinutes);
            SetBetaProfileCombo(cfg.BetaActiveProfile);
            RefreshCustomKeyButtons();
            UpdateProfileKeysLabel();
            UpdateFocusLockStatus();

            if (cfg.BetaProfileWorkEnabled && _profileWork != null && _featureManager != null)
            {
                _featureManager.Toggle(_profileWork);
                BetaProfileWorkToggle.IsChecked = true;
            }

            // Apply focus lock immediately if game is already unfocused
            if (cfg.BetaFocusLock && _windowTracker != null && !_windowTracker.IsFocused)
                _featureManager?.SetInputSuspended(true);
        }

        private void ApplyProfileFromConfig(AppConfig cfg)
        {
            if (_profileWork == null) return;
            var custom = ParseCustomKeys(cfg.BetaCustomKeys);
            _profileWork.SetProfile(cfg.BetaActiveProfile, custom);
        }

        private static List<string> ParseCustomKeys(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (list.Count >= 3) break;
                if (KeyHelper.ToVk(part) != 0)
                    list.Add(KeyHelper.ToName(KeyHelper.ToVk(part)));
            }
            return list;
        }

        private void SaveBetaConfig()
        {
            if (_configManager == null) return;
            var cfg = _configManager.Config;
            cfg.BetaFocusLock = BetaFocusLockToggle.IsChecked == true;
            cfg.BetaProfileWorkEnabled = _profileWork?.IsEnabled == true;
            cfg.BetaActiveProfile = GetSelectedProfileId();
            cfg.BetaCustomKeys = string.Join(",", GetCustomKeysFromButtons());
            cfg.BetaSessionRemindMinutes = GetSelectedRemindMinutes();
            cfg.BetaSessionAutoStopAssists = BetaSessionAutoStopCheck.IsChecked == true;
            _configManager.Save();
        }

        private string GetSelectedProfileId()
        {
            if (BetaProfileCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "Interact";
        }

        private int GetSelectedRemindMinutes()
        {
            if (BetaSessionRemindCombo.SelectedItem is ComboBoxItem item && item.Tag is string t
                && int.TryParse(t, out int m))
                return m;
            return 30;
        }

        private List<string> GetCustomKeysFromButtons()
        {
            var keys = new List<string>();
            foreach (var content in new[] { BetaCustomKey1Btn.Content?.ToString(), BetaCustomKey2Btn.Content?.ToString(), BetaCustomKey3Btn.Content?.ToString() })
            {
                if (string.IsNullOrWhiteSpace(content) || content == "—" || content == "…") continue;
                if (KeyHelper.ToVk(content) != 0)
                    keys.Add(content);
            }
            if (keys.Count == 0) keys.Add("F");
            return keys;
        }

        private void RefreshCustomKeyButtons()
        {
            if (_configManager == null) return;
            var keys = ParseCustomKeys(_configManager.Config.BetaCustomKeys);
            while (keys.Count < 3) keys.Add("");
            BetaCustomKey1Btn.Content = string.IsNullOrEmpty(keys[0]) ? "—" : keys[0];
            BetaCustomKey2Btn.Content = string.IsNullOrEmpty(keys[1]) ? "—" : keys[1];
            BetaCustomKey3Btn.Content = string.IsNullOrEmpty(keys[2]) ? "—" : keys[2];
        }

        private void SetBetaProfileCombo(string profileId)
        {
            foreach (ComboBoxItem item in BetaProfileCombo.Items)
            {
                if (item.Tag is string tag && string.Equals(tag, profileId, StringComparison.OrdinalIgnoreCase))
                {
                    BetaProfileCombo.SelectedItem = item;
                    break;
                }
            }
            BetaCustomKeysPanel.Visibility =
                string.Equals(profileId, "Custom", StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBetaSessionRemindCombo(int minutes)
        {
            foreach (ComboBoxItem item in BetaSessionRemindCombo.Items)
            {
                if (item.Tag is string t && int.TryParse(t, out int m) && m == minutes)
                {
                    BetaSessionRemindCombo.SelectedItem = item;
                    return;
                }
            }
        }

        private void UpdateProfileKeysLabel()
        {
            if (_profileWork == null) return;
            BetaProfileKeysText.Text = $"Holds: {_profileWork.KeysSummary}";
            bool both = _profileWork.IsEnabled && _workAssist?.IsEnabled == true;
            BetaProfileWarnText.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFocusLockStatus()
        {
            bool lockOn = BetaFocusLockToggle.IsChecked == true;
            bool focused = _windowTracker?.IsFocused == true;
            bool suspended = _featureManager?.IsInputSuspended == true;

            if (!lockOn)
            {
                BetaFocusDot.Fill = (SolidColorBrush)FindResource("TextSecondaryBrush");
                BetaFocusStatusText.Text = "Focus lock off";
            }
            else if (suspended || !focused)
            {
                BetaFocusDot.Fill = (SolidColorBrush)FindResource("AccentYellowBrush");
                BetaFocusStatusText.Text = "Suspended (Palworld not focused)";
            }
            else
            {
                BetaFocusDot.Fill = (SolidColorBrush)FindResource("AccentGreenBrush");
                BetaFocusStatusText.Text = "Game focused — assists active";
            }
        }

        private void OnGameFocusChanged(bool focused)
        {
            if (_configManager?.Config.BetaEnabled == true
                && _configManager.Config.BetaFocusLock
                && _featureManager != null)
                _featureManager.SetInputSuspended(!focused);
            UpdateFocusLockStatus();
        }

        private void BetaFocusLockToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_configManager == null || _featureManager == null) return;
            if (_configManager.Config.BetaEnabled != true)
            {
                BetaFocusLockToggle.IsChecked = false;
                return;
            }
            bool on = BetaFocusLockToggle.IsChecked == true;
            _configManager.Config.BetaFocusLock = on;
            if (on)
                _featureManager.SetInputSuspended(_windowTracker?.IsFocused != true);
            else
                _featureManager.SetInputSuspended(false);
            SaveBetaConfig();
            UpdateFocusLockStatus();
        }

        private void BetaProfileWorkToggle_Changed(object s, RoutedEventArgs e)
        {
            if (_profileWork == null || _featureManager == null || _configManager == null) return;
            if (_configManager.Config.BetaEnabled != true)
            {
                BetaProfileWorkToggle.IsChecked = false;
                return;
            }
            bool want = BetaProfileWorkToggle.IsChecked == true;
            if (want != _profileWork.IsEnabled)
                _featureManager.Toggle(_profileWork);
            UpdateProfileKeysLabel();
            SaveBetaConfig();
            UpdateHud();
        }

        private void BetaProfileCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (_configManager == null || _profileWork == null || BetaProfileCombo.SelectedItem == null) return;
            string id = GetSelectedProfileId();
            _configManager.Config.BetaActiveProfile = id;
            BetaCustomKeysPanel.Visibility =
                string.Equals(id, "Custom", StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
            var custom = GetCustomKeysFromButtons();
            _profileWork.SetProfile(id, custom);
            UpdateProfileKeysLabel();
            SaveBetaConfig();
        }

        private void BetaCustomKey1Btn_Click(object s, RoutedEventArgs e) => StartRebind("customKey1");
        private void BetaCustomKey2Btn_Click(object s, RoutedEventArgs e) => StartRebind("customKey2");
        private void BetaCustomKey3Btn_Click(object s, RoutedEventArgs e) => StartRebind("customKey3");

        private void ApplyCustomKeyPick(string target, string keyName)
        {
            if (_configManager == null || _profileWork == null) return;
            var keys = GetCustomKeysFromButtons();
            int idx = target switch { "customKey1" => 0, "customKey2" => 1, _ => 2 };
            while (keys.Count <= idx) keys.Add("");
            // Replace or clear slot
            if (idx < keys.Count)
                keys[idx] = keyName;
            else
                keys.Add(keyName);

            // Compact empties and cap at 3 unique
            var cleaned = new List<string>();
            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k) || k == "—" || k == "…") continue;
                if (!cleaned.Contains(k, StringComparer.OrdinalIgnoreCase) && cleaned.Count < 3)
                    cleaned.Add(k);
            }
            if (cleaned.Count == 0) cleaned.Add(keyName);

            _configManager.Config.BetaCustomKeys = string.Join(",", cleaned);
            _configManager.Config.BetaActiveProfile = "Custom";
            SetBetaProfileCombo("Custom");
            RefreshCustomKeyButtons();
            _profileWork.SetProfile("Custom", cleaned);
            UpdateProfileKeysLabel();
            SaveBetaConfig();
        }

        private void StartSessionTimer()
        {
            _sessionStartedUtc = DateTime.UtcNow;
            _sessionLastRemindUtc = _sessionStartedUtc;
            _sessionReminderActive = false;
            BetaSessionReminderText.Visibility = Visibility.Collapsed;

            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTimer.Tick += (_, _) => OnSessionTick();
            _sessionTimer.Start();
            OnSessionTick();
        }

        private void OnSessionTick()
        {
            var elapsed = DateTime.UtcNow - _sessionStartedUtc;
            BetaSessionElapsedText.Text = $"Session {elapsed:hh\\:mm\\:ss}";

            int minutes = GetSelectedRemindMinutes();
            if (minutes <= 0 || _sessionReminderActive) return;

            var sinceRemind = DateTime.UtcNow - _sessionLastRemindUtc;
            if (sinceRemind.TotalMinutes >= minutes)
                FireSessionReminder();
        }

        private void FireSessionReminder()
        {
            _sessionReminderActive = true;
            _sessionLastRemindUtc = DateTime.UtcNow;
            BetaSessionReminderText.Text = $"Session reminder ({GetSelectedRemindMinutes()} min) — take a break?";
            BetaSessionReminderText.Visibility = Visibility.Visible;

            if (BetaSessionAutoStopCheck.IsChecked == true)
            {
                _featureManager?.DisableAll();
                WorkAssistToggle.IsChecked = false;
                SprintToggle.IsChecked = false;
                BetaProfileWorkToggle.IsChecked = false;
                SyncUI();
            }
        }

        private void BetaSessionRemindCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (_configManager == null) return;
            // Reset reminder baseline when interval changes
            _sessionLastRemindUtc = DateTime.UtcNow;
            _sessionReminderActive = false;
            BetaSessionReminderText.Visibility = Visibility.Collapsed;
            SaveBetaConfig();
        }

        private void BetaSessionAutoStopCheck_Changed(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;
            SaveBetaConfig();
        }

        private void BetaSessionResetBtn_Click(object s, RoutedEventArgs e)
        {
            _sessionStartedUtc = DateTime.UtcNow;
            _sessionLastRemindUtc = _sessionStartedUtc;
            _sessionReminderActive = false;
            BetaSessionReminderText.Visibility = Visibility.Collapsed;
            OnSessionTick();
        }

        private void BetaSessionDismissBtn_Click(object s, RoutedEventArgs e)
        {
            _sessionReminderActive = false;
            _sessionLastRemindUtc = DateTime.UtcNow;
            BetaSessionReminderText.Visibility = Visibility.Collapsed;
        }

        private void StartSetupWizard()
        {
            _wizardStep = 0;
            BetaWizardIdlePanel.Visibility = Visibility.Collapsed;
            BetaWizardActivePanel.Visibility = Visibility.Visible;
            ShowWizardStep();
        }

        private void ShowWizardStep()
        {
            if (_wizardStep < 0 || _wizardStep >= WizardTargets.Length)
            {
                FinishSetupWizard();
                return;
            }
            BetaWizardStepText.Text = $"Step {_wizardStep + 1} of {WizardTargets.Length}";
            BetaWizardPromptText.Text = WizardPrompts[_wizardStep];
            StartRebind(WizardTargets[_wizardStep]);
        }

        private void AdvanceSetupWizard()
        {
            _wizardStep++;
            if (_wizardStep >= WizardTargets.Length)
                FinishSetupWizard();
            else
                ShowWizardStep();
        }

        private void FinishSetupWizard()
        {
            _wizardStep = -1;
            _rebindTarget = null;
            BetaWizardActivePanel.Visibility = Visibility.Collapsed;
            BetaWizardIdlePanel.Visibility = Visibility.Visible;
            if (_configManager != null)
            {
                _configManager.Config.BetaSetupWizardCompleted = true;
                _configManager.Save();
            }
            RefreshHotkeyLabels();
            BetaWizardPromptText.Text = "Setup complete.";
        }

        private void CancelSetupWizard()
        {
            _wizardStep = -1;
            EndRebind();
            BetaWizardActivePanel.Visibility = Visibility.Collapsed;
            BetaWizardIdlePanel.Visibility = Visibility.Visible;
        }

        private void BetaWizardStartBtn_Click(object s, RoutedEventArgs e) => StartSetupWizard();
        private void BetaWizardSkipBtn_Click(object s, RoutedEventArgs e)
        {
            EndRebind();
            AdvanceSetupWizard();
        }
        private void BetaWizardCancelBtn_Click(object s, RoutedEventArgs e) => CancelSetupWizard();

        private async void SaveSettingsBtn_Click(object s, RoutedEventArgs e)
        {
            if (_configManager == null) return;

            // Sync live UI / feature state into the config object before writing
            var cfg = _configManager.Config;
            if (_workAssist != null) cfg.WorkAssistEnabled = _workAssist.IsEnabled;
            cfg.SprintEnabled = SprintAssistAvailable && _sprint != null && _sprint.IsEnabled;
            if (_profileWork != null) cfg.BetaProfileWorkEnabled = _profileWork.IsEnabled;

            cfg.SprintDuration = SprintDurSlider.Value;
            cfg.RecoveryDuration = RecoveryDurSlider.Value;
            cfg.SprintPauseDodge = PauseDodgeCheck.IsChecked == true;
            cfg.HudDraggable = HudDraggableToggle.IsChecked == true;
            cfg.WorkAssistShowHud = WorkAssistShowHudCheck.IsChecked == true;
            cfg.MenuX = Canvas.GetLeft(MenuPanel);
            cfg.MenuY = Canvas.GetTop(MenuPanel);
            cfg.BetaEnabled = BetaEnabledToggle.IsChecked == true;
            cfg.BetaFocusLock = BetaFocusLockToggle.IsChecked == true;
            cfg.BetaActiveProfile = GetSelectedProfileId();
            cfg.BetaCustomKeys = string.Join(",", GetCustomKeysFromButtons());
            cfg.BetaSessionRemindMinutes = GetSelectedRemindMinutes();
            cfg.BetaSessionAutoStopAssists = BetaSessionAutoStopCheck.IsChecked == true;

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
            var result = MessageBox.Show(
                this,
                $"PalAssist v{version} is available.\n\nDownload and install now?\n(The app will restart after installing.)",
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
            if (_profileWork != null)
                BetaProfileWorkToggle.IsChecked = _profileWork.IsEnabled;
            UpdateProfileKeysLabel();
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

            if (_profileWork != null && _profileWork.IsEnabled)
            {
                anyActive = true;
                string status = _profileWork.IsInputSuspended ? "Paused" : _profileWork.KeysSummary;
                AddHudRow("Profile", status, (SolidColorBrush)FindResource("AccentYellowBrush"));
            }

            if (_sessionReminderActive)
            {
                anyActive = true;
                AddHudRow("Session", "Reminder", (SolidColorBrush)FindResource("AccentYellowBrush"));
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
            _uiTimer?.Stop();
            _sessionTimer?.Stop();
            _updateCts?.Cancel();
            SetMenuCursorForced(false);

            if (_configManager != null)
            {
                if (_workAssist != null)  _configManager.Config.WorkAssistEnabled  = _workAssist.IsEnabled;
                _configManager.Config.SprintEnabled = SprintAssistAvailable && _sprint != null && _sprint.IsEnabled;
                if (_profileWork != null) _configManager.Config.BetaProfileWorkEnabled = _profileWork.IsEnabled;
                _configManager.Config.MenuX = Canvas.GetLeft(MenuPanel);
                _configManager.Config.MenuY = Canvas.GetTop(MenuPanel);
                _configManager.Config.BetaEnabled = BetaEnabledToggle.IsChecked == true;
                _configManager.Config.BetaFocusLock = BetaFocusLockToggle.IsChecked == true;
                _configManager.Config.BetaActiveProfile = GetSelectedProfileId();
                _configManager.Config.BetaCustomKeys = string.Join(",", GetCustomKeysFromButtons());
                _configManager.Config.BetaSessionRemindMinutes = GetSelectedRemindMinutes();
                _configManager.Config.BetaSessionAutoStopAssists = BetaSessionAutoStopCheck.IsChecked == true;
                SyncCrosshairConfigFromUi(_configManager.Config);
                _configManager.Save();
            }

            _featureManager?.Dispose();
            _hotkeyManager?.Dispose();
            _windowTracker?.Dispose();
            _updateService?.Dispose();
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
