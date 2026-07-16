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
    /// </summary>
    public class WorkAssistFeature : IFeature
    {
        public string Name        => "Work Assist";
        public string Description => "Continuously holds the F key for work/interactions.";
        public bool   IsEnabled   { get; private set; }
        public bool   IsInputSuspended { get; private set; }

        // Safety re-hold if something released F (focus loss, etc.) — not every frame
        private readonly Stopwatch _reassertTimer = new();
        private const double ReassertIntervalSec = 0.75;

        public void OnEnable()
        {
            IsEnabled = true;
            IsInputSuspended = false;
            PressF();
            _reassertTimer.Restart();
        }

        public void OnDisable()
        {
            IsEnabled = false;
            IsInputSuspended = false;
            ReleaseF();
            _reassertTimer.Reset();
        }

        public void SuspendInput()
        {
            if (!IsEnabled || IsInputSuspended) return;
            IsInputSuspended = true;
            ReleaseF();
            _reassertTimer.Reset();
        }

        public void ResumeInput()
        {
            if (!IsEnabled || !IsInputSuspended) return;
            IsInputSuspended = false;
            PressF();
            _reassertTimer.Restart();
        }

        public void Update()
        {
            if (!IsEnabled || IsInputSuspended) return;

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
