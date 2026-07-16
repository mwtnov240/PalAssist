using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PalAssist.Core;
using PalAssist.Win32;

namespace PalAssist.Features
{
    /// <summary>
    /// Beta multi-key work profiles. Holds one or more keys with the same
    /// press-once + reassert pattern as Work Assist (avoids resetting hold progress).
    /// Independent of the stable Work Assist feature.
    /// </summary>
    public class ProfileWorkFeature : IFeature
    {
        public string Name => "Profile Work";
        public string Description => "Beta multi-key work hold profiles.";
        public bool IsEnabled { get; private set; }
        public bool IsInputSuspended { get; private set; }

        private readonly List<(string Name, ushort Vk, ushort Scan)> _keys = new();
        private readonly Stopwatch _reassertTimer = new();
        private const double ReassertIntervalSec = 0.75;

        /// <summary>Active profile id (Interact, ForwardInteract, Custom).</summary>
        public string ProfileId { get; private set; } = "Interact";

        /// <summary>Human-readable summary of held keys, e.g. "W+F".</summary>
        public string KeysSummary =>
            _keys.Count == 0 ? "(none)" : string.Join("+", _keys.Select(k => k.Name));

        /// <summary>
        /// Apply a built-in or custom profile. Releases previous keys if currently enabled.
        /// </summary>
        public void SetProfile(string profileId, IReadOnlyList<string>? customKeyNames = null)
        {
            bool wasEnabled = IsEnabled && !IsInputSuspended;
            if (wasEnabled)
                ReleaseAll();

            ProfileId = profileId;
            _keys.Clear();

            IEnumerable<string> names = profileId switch
            {
                "ForwardInteract" => new[] { "W", "F" },
                "Custom" => (customKeyNames != null && customKeyNames.Count > 0)
                    ? customKeyNames.Take(3)
                    : new[] { "F" },
                _ => new[] { "F" } // Interact
            };

            foreach (string raw in names)
            {
                string name = raw.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                uint vk = KeyHelper.ToVk(name);
                if (vk == 0) continue;
                ushort scan = (ushort)NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
                if (_keys.Any(k => k.Vk == (ushort)vk)) continue;
                _keys.Add((KeyHelper.ToName(vk), (ushort)vk, scan));
            }

            if (_keys.Count == 0)
                _keys.Add(("F", NativeMethods.VK_F, NativeMethods.SCAN_F));

            if (wasEnabled)
            {
                PressAll();
                _reassertTimer.Restart();
            }
        }

        public void OnEnable()
        {
            IsEnabled = true;
            IsInputSuspended = false;
            if (_keys.Count == 0)
                SetProfile("Interact");
            PressAll();
            _reassertTimer.Restart();
        }

        public void OnDisable()
        {
            IsEnabled = false;
            IsInputSuspended = false;
            ReleaseAll();
            _reassertTimer.Reset();
        }

        public void SuspendInput()
        {
            if (!IsEnabled || IsInputSuspended) return;
            IsInputSuspended = true;
            ReleaseAll();
            _reassertTimer.Reset();
        }

        public void ResumeInput()
        {
            if (!IsEnabled || !IsInputSuspended) return;
            IsInputSuspended = false;
            PressAll();
            _reassertTimer.Restart();
        }

        public void Update()
        {
            if (!IsEnabled || IsInputSuspended) return;

            bool anyUp = false;
            foreach (var k in _keys)
            {
                if (!NativeMethods.IsKeyDown(k.Vk))
                {
                    anyUp = true;
                    break;
                }
            }

            if (!anyUp) return;

            if (_reassertTimer.Elapsed.TotalSeconds >= ReassertIntervalSec || !_reassertTimer.IsRunning)
            {
                PressAll();
                _reassertTimer.Restart();
            }
        }

        private void PressAll()
        {
            foreach (var k in _keys)
                InputSimulator.KeyDown(k.Vk, k.Scan);
        }

        private void ReleaseAll()
        {
            foreach (var k in _keys)
                InputSimulator.KeyUp(k.Vk, k.Scan);
        }
    }
}
