using PalAssist.Core;
using PalAssist.Win32;

namespace PalAssist.Features
{
    /// <summary>
    /// Hold E Assist — continuously holds the E key while enabled.
    /// Uses scan-code based input so DirectInput games (like Palworld) accept it.
    /// </summary>
    public class HoldEFeature : IFeature
    {
        public string Name        => "Hold E Assist";
        public string Description => "Continuously holds the E key for interactions.";
        public bool   IsEnabled   { get; private set; }

        /// <summary>
        /// Sends an initial E key-down event when the feature is turned on.
        /// The Update() loop re-sends it periodically to keep the game happy.
        /// </summary>
        public void OnEnable()
        {
            IsEnabled = true;
            InputSimulator.KeyDown(NativeMethods.SCAN_E);
        }

        /// <summary>
        /// Releases the E key cleanly when the feature is turned off.
        /// </summary>
        public void OnDisable()
        {
            IsEnabled = false;
            InputSimulator.KeyUp(NativeMethods.SCAN_E);
        }

        /// <summary>
        /// Re-sends the key-down event each tick.
        /// Some games require periodic re-sending to register a continuous hold.
        /// </summary>
        public void Update()
        {
            // Re-press E on every tick to ensure the game keeps registering the hold.
            InputSimulator.KeyDown(NativeMethods.SCAN_E);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  EXAMPLE: How to add a second feature
    // ──────────────────────────────────────────────────────────────────
    //
    //  1. Create a new file, e.g. AutoRunFeature.cs
    //  2. Implement IFeature:
    //
    //      public class AutoRunFeature : IFeature
    //      {
    //          public string Name        => "Auto Run";
    //          public string Description => "Toggles auto-run (holds W).";
    //          public bool   IsEnabled   { get; private set; }
    //
    //          public void OnEnable()  { IsEnabled = true;  InputSimulator.KeyDown(0x11); } // 0x11 = W scan code
    //          public void OnDisable() { IsEnabled = false; InputSimulator.KeyUp(0x11);   }
    //          public void Update()    { InputSimulator.KeyDown(0x11); }
    //      }
    //
    //  3. Register it in MainWindow.xaml.cs → InitializeFeatures():
    //
    //      var autoRun = new AutoRunFeature();
    //      _featureManager.Register(autoRun);
    //
    //  4. Add a UI toggle row in MainWindow.xaml (copy the Hold E row).
    //
    //  Done! No other plumbing needed.
    // ──────────────────────────────────────────────────────────────────
}
