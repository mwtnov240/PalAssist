using System;
using System.Diagnostics;
using PalAssist.Core;
using PalAssist.Win32;

namespace PalAssist.Features
{
    /// <summary>
    /// Work Assist — holds the F key (Palworld work/interact) while enabled.
    ///
    /// Important: do NOT re-send KeyDown every tick. Palworld's hold-to-work
    /// progress resets if F is "pressed" again each frame, so nothing ever finishes.
    /// We press once and only re-assert if the OS reports F is no longer down.
    ///
    /// Smart Work Assist (beta): on enable, tap F once, waits N ms, then hold —
    /// so items sitting on a workstation are picked up before work starts.
    ///
    /// Active Hold (beta): when the game is not focused, release global F (so other
    /// apps are clean) and deliver F only to the Palworld window via PostMessage.
    /// </summary>
    public class WorkAssistFeature : IFeature
    {
        public string Name        => "Work Assist";
        public string Description => "Continuously holds the F key for work/interactions.";
        public bool   IsEnabled   { get; private set; }
        public bool   IsInputSuspended { get; private set; }

        /// <summary>
        /// When true, OnEnable runs tap → wait → hold instead of immediate hold.
        /// Set from Beta → Smart Work Assist; does not change Work Assist on/off UX.
        /// </summary>
        public bool SmartPickupEnabled { get; set; }

        /// <summary>
        /// When true, Work Assist continues while Palworld is unfocused by posting
        /// F only to the game window (does not type into other programs).
        /// </summary>
        public bool ActiveHoldEnabled { get; set; }

        /// <summary>True while delivering F via window-targeted messages.</summary>
        public bool IsBackgroundHold { get; private set; }

        /// <summary>
        /// Wait after the pickup tap before continuous hold starts (0–1000 ms).
        /// 0 = hold immediately after the short tap; 1000 = 1 second.
        /// </summary>
        public int SmartWaitAfterTapMs
        {
            get => _smartWaitAfterTapMs;
            set => _smartWaitAfterTapMs = Math.Clamp(value, 0, 1000);
        }

        private int _smartWaitAfterTapMs = 500;

        private enum WorkPhase
        {
            Holding,
            SmartTap,
            SmartWait,
            BackgroundHold
        }

        private WorkPhase _phase = WorkPhase.Holding;
        private readonly Stopwatch _phaseTimer = new();
        private const double SmartTapDurationSec = 0.05;

        // Safety re-hold if something released F (focus loss, etc.) — not every frame
        private readonly Stopwatch _reassertTimer = new();
        private const double ReassertIntervalSec = 0.75;

        // Background PostMessage cadence (~30 Hz is enough; avoid spamming the queue)
        private readonly Stopwatch _bgPostTimer = new();
        private const double BackgroundPostIntervalSec = 0.05;
        private bool _bgFirstPost = true;
        private IntPtr _bgHwnd = IntPtr.Zero;

        public void OnEnable()
        {
            IsEnabled = true;
            IsInputSuspended = false;
            IsBackgroundHold = false;
            _bgHwnd = IntPtr.Zero;

            if (SmartPickupEnabled)
                BeginSmartSequence();
            else
                BeginHolding();
        }

        public void OnDisable()
        {
            IsEnabled = false;
            IsInputSuspended = false;
            LeaveBackgroundHold(releaseWindowKey: true);
            ResetPhase();
            ReleaseF();
            _reassertTimer.Reset();
        }

        public void SuspendInput()
        {
            if (!IsEnabled || IsInputSuspended) return;
            IsInputSuspended = true;
            LeaveBackgroundHold(releaseWindowKey: true);
            ResetPhase();
            ReleaseF();
            _reassertTimer.Reset();
        }

        public void ResumeInput()
        {
            if (!IsEnabled || !IsInputSuspended) return;
            IsInputSuspended = false;
            // Do not re-run smart pickup on focus return (avoids accidental pickups every alt-tab)
            BeginHolding();
        }

        /// <summary>
        /// Switch to window-targeted F hold so other apps are not affected.
        /// Releases the global F key state first.
        /// </summary>
        public void EnterBackgroundHold(IntPtr gameHwnd)
        {
            if (!IsEnabled) return;

            // If we were fully suspended, leave that state without SendInput re-hold
            IsInputSuspended = false;

            if (gameHwnd == IntPtr.Zero || !NativeMethods.IsWindow(gameHwnd))
            {
                // No target — safest is to stop injecting globally
                LeaveBackgroundHold(releaseWindowKey: false);
                ResetPhase();
                ReleaseF();
                _reassertTimer.Reset();
                IsInputSuspended = true;
                return;
            }

            // Drop global F so Discord/Chrome/etc. do not see a stuck F
            ReleaseF();
            _reassertTimer.Reset();

            _bgHwnd = gameHwnd;
            IsBackgroundHold = true;
            _phase = WorkPhase.BackgroundHold;
            _phaseTimer.Restart();
            _bgPostTimer.Restart();
            _bgFirstPost = true;

            // Immediate first post so work does not gap on alt-tab
            PostBackgroundF(first: true);
            _bgFirstPost = false;
        }

        /// <summary>
        /// Leave background mode and resume normal SendInput hold (no Smart re-tap).
        /// </summary>
        public void ExitBackgroundHoldToForeground()
        {
            if (!IsEnabled) return;
            LeaveBackgroundHold(releaseWindowKey: true);
            IsInputSuspended = false;
            BeginHolding();
        }

        /// <summary>Update game HWND while already in background hold (handle refresh).</summary>
        public void UpdateBackgroundHwnd(IntPtr gameHwnd)
        {
            if (!IsBackgroundHold) return;
            if (gameHwnd == IntPtr.Zero || !NativeMethods.IsWindow(gameHwnd))
            {
                SuspendInput();
                return;
            }
            _bgHwnd = gameHwnd;
        }

        public void Update()
        {
            if (!IsEnabled || IsInputSuspended) return;

            switch (_phase)
            {
                case WorkPhase.SmartTap:
                    if (_phaseTimer.Elapsed.TotalSeconds >= SmartTapDurationSec)
                    {
                        ReleaseF();
                        _phase = WorkPhase.SmartWait;
                        _phaseTimer.Restart();
                    }
                    return;

                case WorkPhase.SmartWait:
                    if (_phaseTimer.Elapsed.TotalMilliseconds >= SmartWaitAfterTapMs)
                        BeginHolding();
                    return;

                case WorkPhase.BackgroundHold:
                    if (_bgHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_bgHwnd))
                    {
                        SuspendInput();
                        return;
                    }

                    if (_bgPostTimer.Elapsed.TotalSeconds >= BackgroundPostIntervalSec || !_bgPostTimer.IsRunning)
                    {
                        PostBackgroundF(first: _bgFirstPost);
                        _bgFirstPost = false;
                        _bgPostTimer.Restart();
                    }
                    return;

                case WorkPhase.Holding:
                default:
                    // If F is still held according to the OS, leave it alone.
                    bool fDown = NativeMethods.IsKeyDown(NativeMethods.VK_F);
                    if (fDown)
                        return;

                    // Only re-press if F was released (or never registered)
                    if (_reassertTimer.Elapsed.TotalSeconds >= ReassertIntervalSec || !_reassertTimer.IsRunning)
                    {
                        PressF();
                        _reassertTimer.Restart();
                    }
                    return;
            }
        }

        private void BeginSmartSequence()
        {
            LeaveBackgroundHold(releaseWindowKey: false);
            ReleaseF();
            PressF();
            _phase = WorkPhase.SmartTap;
            _phaseTimer.Restart();
            _reassertTimer.Reset();
        }

        private void BeginHolding()
        {
            LeaveBackgroundHold(releaseWindowKey: false);
            _phase = WorkPhase.Holding;
            _phaseTimer.Reset();
            PressF();
            _reassertTimer.Restart();
        }

        private void ResetPhase()
        {
            _phase = WorkPhase.Holding;
            _phaseTimer.Reset();
        }

        private void LeaveBackgroundHold(bool releaseWindowKey)
        {
            if (!IsBackgroundHold && _bgHwnd == IntPtr.Zero)
            {
                IsBackgroundHold = false;
                return;
            }

            if (releaseWindowKey && _bgHwnd != IntPtr.Zero && NativeMethods.IsWindow(_bgHwnd))
            {
                try
                {
                    InputSimulator.PostKeyUp(_bgHwnd, NativeMethods.VK_F, NativeMethods.SCAN_F);
                }
                catch
                {
                    // best-effort
                }
            }

            IsBackgroundHold = false;
            _bgHwnd = IntPtr.Zero;
            _bgPostTimer.Reset();
            _bgFirstPost = true;
        }

        private void PostBackgroundF(bool first)
        {
            if (_bgHwnd == IntPtr.Zero) return;
            try
            {
                if (first)
                    InputSimulator.PostKeyDown(_bgHwnd, NativeMethods.VK_F, NativeMethods.SCAN_F);
                else
                    InputSimulator.PostKeyDownRepeat(_bgHwnd, NativeMethods.VK_F, NativeMethods.SCAN_F);
            }
            catch
            {
                // best-effort; next tick will retry or suspend if hwnd dies
            }
        }

        private static void PressF()
        {
            InputSimulator.KeyDown(NativeMethods.VK_F, NativeMethods.SCAN_F);
        }

        private static void ReleaseF()
        {
            InputSimulator.KeyUp(NativeMethods.VK_F, NativeMethods.SCAN_F);
        }
    }
}
