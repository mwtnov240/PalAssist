namespace PalAssist.Features
{
    /// <summary>
    /// Contract for every assist feature in PalAssist.
    /// To add a new feature:
    ///   1. Create a class that implements IFeature.
    ///   2. Register it in FeatureManager.
    ///   3. Add a UI toggle in MainWindow.xaml.
    /// That's it — no other wiring required.
    /// </summary>
    public interface IFeature
    {
        /// <summary>Display name shown in the overlay menu.</summary>
        string Name { get; }

        /// <summary>Short description shown as a tooltip or subtitle.</summary>
        string Description { get; }

        /// <summary>Whether the feature is currently active.</summary>
        bool IsEnabled { get; }

        /// <summary>
        /// True when input is temporarily released (e.g. focus-lock / alt-tab)
        /// without clearing <see cref="IsEnabled"/>.
        /// </summary>
        bool IsInputSuspended { get; }

        /// <summary>Called once when the feature is turned on.</summary>
        void OnEnable();

        /// <summary>Called once when the feature is turned off.</summary>
        void OnDisable();

        /// <summary>
        /// Release held keys but keep the feature enabled (focus-lock).
        /// </summary>
        void SuspendInput();

        /// <summary>
        /// Re-assert held keys after <see cref="SuspendInput"/> if still enabled.
        /// </summary>
        void ResumeInput();

        /// <summary>
        /// Called on every update tick (~60 Hz) while the feature is enabled
        /// and not suspended. Use this for continuous actions like holding a key.
        /// </summary>
        void Update();
    }
}
