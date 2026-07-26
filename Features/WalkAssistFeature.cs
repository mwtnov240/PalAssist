using System.Diagnostics;
using PalAssist.Core;
using PalAssist.Win32;

namespace PalAssist.Features
{
    /// <summary>
    /// Walk Assist — holds the W key (forward movement) while enabled.
    ///
    /// Same hold backend as Work Assist: press once on enable, and only re-assert
    /// if the OS reports W is no longer down. Do not spam KeyDown every tick.
    /// </summary>
    public class WalkAssistFeature : IFeature
    {
        public string Name        => "Walk Assist";
        public string Description => "Continuously holds the W key for forward walk.";
        public bool   IsEnabled   { get; private set; }
        public bool   IsInputSuspended { get; private set; }

        // Safety re-hold if something released W (focus loss, etc.) — not every frame
        private readonly Stopwatch _reassertTimer = new();
        private const double ReassertIntervalSec = 0.75;

        public void OnEnable()
        {
            IsEnabled = true;
            IsInputSuspended = false;
            PressW();
            _reassertTimer.Restart();
        }

        public void OnDisable()
        {
            IsEnabled = false;
            IsInputSuspended = false;
            ReleaseW();
            _reassertTimer.Reset();
        }

        public void SuspendInput()
        {
            if (!IsEnabled || IsInputSuspended) return;
            IsInputSuspended = true;
            ReleaseW();
            _reassertTimer.Reset();
        }

        public void ResumeInput()
        {
            if (!IsEnabled || !IsInputSuspended) return;
            IsInputSuspended = false;
            PressW();
            _reassertTimer.Restart();
        }

        public void Update()
        {
            if (!IsEnabled || IsInputSuspended) return;

            // If W is still held according to the OS, leave it alone.
            if (NativeMethods.IsKeyDown(NativeMethods.VK_W))
                return;

            // Only re-press if W was released (or never registered)
            if (_reassertTimer.Elapsed.TotalSeconds >= ReassertIntervalSec || !_reassertTimer.IsRunning)
            {
                PressW();
                _reassertTimer.Restart();
            }
        }

        private static void PressW()
        {
            InputSimulator.KeyDown(NativeMethods.VK_W, NativeMethods.SCAN_W);
        }

        private static void ReleaseW()
        {
            InputSimulator.KeyUp(NativeMethods.VK_W, NativeMethods.SCAN_W);
        }
    }
}
