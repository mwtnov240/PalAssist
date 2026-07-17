using System;
using System.Collections.Generic;
using System.Media;

namespace PalAssist.Core
{
    /// <summary>
    /// Optional UI/system sound feedback with per-category toggles and rate limiting.
    /// </summary>
    public sealed class SoundService
    {
        public bool Enabled { get; set; } = true;
        public bool OnToggle { get; set; } = true;
        public bool OnFocus { get; set; } = false;
        public bool OnUpdate { get; set; } = true;

        private readonly Dictionary<string, DateTime> _lastPlayUtc = new(StringComparer.Ordinal);
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(150);

        public void PlayToggle(bool enabled)
        {
            if (!Enabled || !OnToggle) return;
            Play("toggle", enabled ? SystemSounds.Asterisk : SystemSounds.Hand);
        }

        public void PlayFocus(bool suspended)
        {
            if (!Enabled || !OnFocus) return;
            Play("focus", suspended ? SystemSounds.Hand : SystemSounds.Asterisk);
        }

        public void PlayUpdate()
        {
            if (!Enabled || !OnUpdate) return;
            Play("update", SystemSounds.Exclamation);
        }

        public void PlayReminder()
        {
            if (!Enabled) return;
            Play("reminder", SystemSounds.Exclamation);
        }

        private void Play(string key, SystemSound sound)
        {
            var now = DateTime.UtcNow;
            if (_lastPlayUtc.TryGetValue(key, out var last) && now - last < MinInterval)
                return;
            _lastPlayUtc[key] = now;
            try
            {
                sound.Play();
            }
            catch
            {
                // Audio device missing / blocked — ignore
            }
        }
    }
}
