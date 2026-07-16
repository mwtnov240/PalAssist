using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PalAssist.Core;
using PalAssist.Features;
using PalAssist.Win32;

namespace PalAssist
{
    public partial class MainWindow : Window
    {
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

        // ── State ──
        private bool   _menuVisible = false;
        private IntPtr _hwnd;

        // ── Hotkey IDs ──
        private int _menuHotkeyId   = -1;
        private int _workAssistHotkeyId  = -1;
        private int _sprintHotkeyId = -1;

        // ── Rebind state ──
        private string? _rebindTarget = null;

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

            // Tool window, no-activate, initially click-through
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
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

            _sprint = new SprintAssistFeature
            {
                SprintDurationSec  = cfg.SprintDuration,
                RecoveryDurationSec = cfg.RecoveryDuration,
                PauseDodge         = cfg.SprintPauseDodge
            };
            _sprint.StateChanged += () => Dispatcher.Invoke(SyncSprintStatus);
            _featureManager.Register(_sprint);

            _featureManager.StateChanged += () => Dispatcher.Invoke(SyncUI);
            _featureManager.Start();

            // ── Window tracker ──
            _windowTracker = new WindowTracker();
            _windowTracker.BoundsChanged += rect => Dispatcher.Invoke(() => AlignToGame(rect));
            _windowTracker.GameDetected  += found => Dispatcher.Invoke(() => OnGameDetected(found));
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

            if (cfg.WorkAssistEnabled) { _featureManager.Toggle(_workAssist); WorkAssistToggle.IsChecked = true; }
            if (cfg.SprintEnabled) { _featureManager.Toggle(_sprint); SprintToggle.IsChecked = true; }

            // ── Listen for rebind keypresses ──
            PreviewKeyDown += OnPreviewKeyDown;

            // ── Set active tab ──
            SwitchTab("assists");

            // ── Position ──
            this.Left   = 0;
            this.Top    = 0;
            this.Width  = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;

            this.SizeChanged += (_, _) => { PositionHud(); ClampMenuToCanvas(); };

            // ── UI timer for sprint status refresh (4 Hz is enough) ──
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _uiTimer.Tick += (_, _) => SyncSprintStatus();
            _uiTimer.Start();

            // Start with menu visible by default on startup
            _menuVisible = true;
            MenuPanel.Visibility = Visibility.Visible;
            SetClickThrough(false);

            UpdateHud();
            PositionHud();

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
            VersionText.Text = $"PalAssist v{UpdateService.GetCurrentVersion()}";
            UpdateStatusText.Text = "";
            InstallUpdateBtn.Visibility = Visibility.Collapsed;
            InstallUpdateBtn.IsEnabled = false;

            // Always auto-check on boot; prompt before download/install
            if (cfg.AutoCheckUpdates)
            {
                _ = BootUpdateCheckAsync();
            }
        }

        // ─────────────────────────────────────────────────
        //  Tab switching
        // ─────────────────────────────────────────────────

        private void TabAssists_Click(object s, RoutedEventArgs e) => SwitchTab("assists");
        private void TabAI_Click(object s, RoutedEventArgs e)      => SwitchTab("ai");
        private void TabSettings_Click(object s, RoutedEventArgs e) => SwitchTab("settings");

        private void SwitchTab(string tab)
        {
            PanelAssists.Visibility  = tab == "assists"  ? Visibility.Visible : Visibility.Collapsed;
            PanelAI.Visibility       = tab == "ai"       ? Visibility.Visible : Visibility.Collapsed;
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

            uint sprintVk = KeyHelper.ToVk(cfg.HotkeySprint);
            if (sprintVk != 0) _sprintHotkeyId = _hotkeyManager.Register(sprintVk, 0, OnSprintHotkeyPressed);

            RefreshHotkeyLabels();
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
            FooterSprintKey.Text = cfg.HotkeySprint;
        }

        // ─────────────────────────────────────────────────
        //  Hotkey callbacks
        // ─────────────────────────────────────────────────

        private void OnMenuHotkeyPressed()
        {
            _menuVisible = !_menuVisible;
            MenuPanel.Visibility = _menuVisible ? Visibility.Visible : Visibility.Collapsed;
            SetClickThrough(!_menuVisible);
            if (_menuVisible) CentreMenuIfNeeded();
        }

        private void OnWorkAssistHotkeyPressed()
        {
            if (_workAssist == null || _featureManager == null) return;
            _featureManager.Toggle(_workAssist);
            WorkAssistToggle.IsChecked = _workAssist.IsEnabled;
        }

        private void OnSprintHotkeyPressed()
        {
            if (_sprint == null || _featureManager == null) return;
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
            var btn = target switch { "menu" => RebindMenuBtn, "workAssist" => RebindWorkAssistBtn, _ => RebindSprintBtn };
            btn.Content = "Press a key…";

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
            if (key == Key.Escape) { EndRebind(); return; }

            uint vk = KeyHelper.WpfKeyToVk(key);
            string name = KeyHelper.WpfKeyToName(key);

            // Unknown key → cancel
            if (vk == 0) { EndRebind(); return; }

            // Save target before EndRebind clears _rebindTarget
            string savedTarget = _rebindTarget!;

            ref int id = ref _menuHotkeyId;
            if (savedTarget == "workAssist")  id = ref _workAssistHotkeyId;
            if (savedTarget == "sprint") id = ref _sprintHotkeyId;

            // Restore WS_EX_NOACTIVATE BEFORE re-registering so hotkey fires correctly
            EndRebind();
            ApplyRebind(ref id, vk, name, savedTarget);
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
        }

        private void OnGameDetected(bool found)
        {
            GameDot.Fill = found
                ? (SolidColorBrush)FindResource("AccentGreenBrush")
                : (SolidColorBrush)FindResource("AccentRedBrush");
            GameStatusText.Text = found ? "Palworld detected" : "Game not found";
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
            if (_sprint == null || _featureManager == null) return;
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

        // Save Settings is intentionally disabled for now (UI shows greyed out).
        // Settings continue to auto-save on change / close.

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
            VersionText.Text = $"PalAssist v{UpdateService.GetCurrentVersion()}";

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
            _uiTimer?.Stop();
            _updateCts?.Cancel();

            if (_configManager != null)
            {
                if (_workAssist != null)  _configManager.Config.WorkAssistEnabled  = _workAssist.IsEnabled;
                if (_sprint != null) _configManager.Config.SprintEnabled = _sprint.IsEnabled;
                _configManager.Config.MenuX = Canvas.GetLeft(MenuPanel);
                _configManager.Config.MenuY = Canvas.GetTop(MenuPanel);
                _configManager.Save();
            }

            _featureManager?.Dispose();
            _hotkeyManager?.Dispose();
            _windowTracker?.Dispose();
            _updateService?.Dispose();
        }
    }
}
