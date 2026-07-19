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
    /// Smart Work Assist (beta): on enable, tap F once, wait N ms, then hold —
    /// so items sitting on a workstation are picked up before work starts.
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
            SmartWait
        }

        private WorkPhase _phase = WorkPhase.Holding;
        private readonly Stopwatch _phaseTimer = new();
        private const double SmartTapDurationSec = 0.05;

        // Safety re-hold if something released F (focus loss, etc.) — not every frame
        private readonly Stopwatch _reassertTimer = new();
        private const double ReassertIntervalSec = 0.75;

        public void OnEnable()
        {
            IsEnabled = true;
            IsInputSuspended = false;

            if (SmartPickupEnabled)
                BeginSmartSequence();
            else
                BeginHolding();
        }

        public void OnDisable()
        {
            IsEnabled = false;
            IsInputSuspended = false;
            ResetPhase();
            ReleaseF();
            _reassertTimer.Reset();
        }

        public void SuspendInput()
        {
            if (!IsEnabled || IsInputSuspended) return;
            IsInputSuspended = true;
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
            ReleaseF();
            PressF();
            _phase = WorkPhase.SmartTap;
            _phaseTimer.Restart();
            _reassertTimer.Reset();
        }

        private void BeginHolding()
        {
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
