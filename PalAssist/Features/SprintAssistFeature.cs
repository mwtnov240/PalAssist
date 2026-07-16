using System;
using System.Diagnostics;
using PalAssist.Core;
using PalAssist.Win32;

namespace PalAssist.Features
{
    /// <summary>
    /// Sprint Assist — automated forward movement with timed sprint/recovery cycles.
    ///
    /// State machine:
    ///   Idle → (OnEnable) → Sprinting → (timer expires) → Recovering → (timer expires) → Sprinting → …
    ///   Any state → (Ctrl detected + PauseDodge on) → DodgePausing → (1.0s) → previous cycling state
    ///
    /// While active, W is held down continuously. Shift is held during Sprinting and
    /// released during Recovering so stamina can regenerate.
    /// </summary>
    public class SprintAssistFeature : IFeature
    {
        public string Name        => "Sprint Assist";
        public string Description => "Auto walks forward and manages sprint/walk cycles.";
        public bool   IsEnabled   { get; private set; }
        public bool   IsInputSuspended { get; private set; }

        // ── Configurable parameters (set from UI before enabling) ──
        public double SprintDurationSec  { get; set; } = 8.0;
        public double RecoveryDurationSec { get; set; } = 4.0;
        public bool   PauseDodge         { get; set; } = false;

        /// <summary>Current phase of the sprint cycle, exposed for UI display.</summary>
        public SprintState State { get; private set; } = SprintState.Idle;

        // ── Timing ──
        private readonly Stopwatch _phaseTimer = new();
        private readonly Stopwatch _dodgeTimer = new();

        // Duration of the dodge-pause (1.0 second as agreed)
        private const double DodgePauseSec = 1.0;

        // Which state to return to after dodge pause
        private SprintState _preDogeState;

        /// <summary>Raised when the sprint state changes so the UI can update.</summary>
        public event Action? StateChanged;

        // ─────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────

        public void OnEnable()
        {
            IsEnabled = true;
            IsInputSuspended = false;

            // Start holding W immediately
            InputSimulator.KeyDown(NativeMethods.SCAN_W);

            // Enter the Sprinting phase
            EnterState(SprintState.Sprinting);
        }

        public void OnDisable()
        {
            IsEnabled = false;
            IsInputSuspended = false;

            // Release ALL keys we might be holding
            InputSimulator.KeyUp(NativeMethods.SCAN_SHIFT);
            InputSimulator.KeyUp(NativeMethods.SCAN_W);

            EnterState(SprintState.Idle);
            _phaseTimer.Stop();
            _dodgeTimer.Stop();
        }

        public void SuspendInput()
        {
            if (!IsEnabled || IsInputSuspended) return;
            IsInputSuspended = true;
            InputSimulator.KeyUp(NativeMethods.SCAN_SHIFT);
            InputSimulator.KeyUp(NativeMethods.SCAN_W);
            _phaseTimer.Stop();
            _dodgeTimer.Stop();
        }

        public void ResumeInput()
        {
            if (!IsEnabled || !IsInputSuspended) return;
            IsInputSuspended = false;

            // Resume cycling from sprint phase with W held
            InputSimulator.KeyDown(NativeMethods.SCAN_W);
            if (State == SprintState.Idle)
                EnterState(SprintState.Sprinting);
            else
            {
                // Re-enter current phase timers so sprint/recovery continues
                if (State == SprintState.Sprinting)
                    InputSimulator.KeyDown(NativeMethods.SCAN_SHIFT);
                _phaseTimer.Restart();
            }
        }

        // ─────────────────────────────────────────────
        //  Update (called ~60 Hz by FeatureManager)
        // ─────────────────────────────────────────────

        public void Update()
        {
            if (!IsEnabled || IsInputSuspended) return;

            // ── Dodge pause detection ──
            if (PauseDodge && State != SprintState.DodgePausing)
            {
                if (NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL))
                {
                    // Save current cycling state and pause
                    _preDogeState = State;
                    InputSimulator.KeyUp(NativeMethods.SCAN_SHIFT);
                    InputSimulator.KeyUp(NativeMethods.SCAN_W);
                    _dodgeTimer.Restart();
                    EnterState(SprintState.DodgePausing);
                    return;
                }
            }

            switch (State)
            {
                case SprintState.Sprinting:
                    // Re-send W + Shift each tick to keep the game happy
                    InputSimulator.KeyDown(NativeMethods.SCAN_W);
                    InputSimulator.KeyDown(NativeMethods.SCAN_SHIFT);

                    if (_phaseTimer.Elapsed.TotalSeconds >= SprintDurationSec)
                    {
                        // Transition to recovery: release Shift, keep holding W
                        InputSimulator.KeyUp(NativeMethods.SCAN_SHIFT);
                        EnterState(SprintState.Recovering);
                    }
                    break;

                case SprintState.Recovering:
                    // Keep holding W, Shift is already released
                    InputSimulator.KeyDown(NativeMethods.SCAN_W);

                    if (_phaseTimer.Elapsed.TotalSeconds >= RecoveryDurationSec)
                    {
                        // Transition back to sprinting
                        EnterState(SprintState.Sprinting);
                    }
                    break;

                case SprintState.DodgePausing:
                    // All keys released — wait for the dodge animation to finish
                    if (_dodgeTimer.Elapsed.TotalSeconds >= DodgePauseSec)
                    {
                        // Resume: re-hold W and restore previous state
                        InputSimulator.KeyDown(NativeMethods.SCAN_W);
                        EnterState(_preDogeState);
                    }
                    break;
            }
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        private void EnterState(SprintState newState)
        {
            State = newState;
            _phaseTimer.Restart();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Possible states in the sprint cycle.</summary>
    public enum SprintState
    {
        Idle,
        Sprinting,
        Recovering,
        DodgePausing
    }
}
