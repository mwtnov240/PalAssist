using System;
using System.Collections.Generic;
using System.Timers;

namespace PalAssist.Features
{
    /// <summary>
    /// Central registry for all assist features.
    /// Manages feature lifetime, tick loop, and toggle operations.
    /// </summary>
    public sealed class FeatureManager : IDisposable
    {
        private readonly List<IFeature> _features = new();
        private readonly System.Timers.Timer _tickTimer;
        private bool _disposed;

        /// <summary>Read-only view of all registered features.</summary>
        public IReadOnlyList<IFeature> Features => _features;

        /// <summary>Raised whenever any feature's enabled state changes.</summary>
        public event Action? StateChanged;

        public FeatureManager()
        {
            // ~60 ticks per second for smooth continuous actions
            _tickTimer = new System.Timers.Timer(16);
            _tickTimer.Elapsed += OnTick;
            _tickTimer.AutoReset = true;
        }

        /// <summary>Register a new feature.</summary>
        public void Register(IFeature feature)
        {
            _features.Add(feature);
        }

        /// <summary>Start the update loop.</summary>
        public void Start() => _tickTimer.Start();

        /// <summary>Stop the update loop.</summary>
        public void Stop() => _tickTimer.Stop();

        /// <summary>Toggle a feature on or off.</summary>
        public void Toggle(IFeature feature)
        {
            if (feature.IsEnabled)
                feature.OnDisable();
            else
                feature.OnEnable();

            StateChanged?.Invoke();
        }

        /// <summary>Disable all features (cleanup).</summary>
        public void DisableAll()
        {
            foreach (var f in _features)
            {
                if (f.IsEnabled)
                    f.OnDisable();
            }
            StateChanged?.Invoke();
        }

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            foreach (var f in _features)
            {
                if (f.IsEnabled)
                    f.Update();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _tickTimer.Stop();
            _tickTimer.Dispose();
            DisableAll();
        }
    }
}
