using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PalAssist.Core
{
    /// <summary>
    /// Applies named color themes by replacing Application resource Color/Brush keys.
    /// </summary>
    public static class ThemeService
    {
        public static readonly string[] ThemeNames = { "Cyan", "Purple", "Green", "Amber", "Red" };

        private sealed class ThemePalette
        {
            public Color BgDark { get; init; }
            public Color BgCard { get; init; }
            public Color AccentBlue { get; init; }
            public Color AccentCyan { get; init; }
            public Color AccentGreen { get; init; }
            public Color AccentRed { get; init; }
            public Color AccentYellow { get; init; }
            public Color TextPrimary { get; init; }
            public Color TextSecondary { get; init; }
        }

        private static readonly Dictionary<string, ThemePalette> Palettes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Cyan"] = new ThemePalette
                {
                    BgDark = Hex("#1A1A2E"),
                    BgCard = Hex("#16213E"),
                    AccentBlue = Hex("#0F3460"),
                    AccentCyan = Hex("#00D2FF"),
                    AccentGreen = Hex("#00E676"),
                    AccentRed = Hex("#FF1744"),
                    AccentYellow = Hex("#FFD600"),
                    TextPrimary = Hex("#E0E0E0"),
                    TextSecondary = Hex("#90A4AE")
                },
                ["Purple"] = new ThemePalette
                {
                    BgDark = Hex("#1A1228"),
                    BgCard = Hex("#241B3A"),
                    AccentBlue = Hex("#3D2A6B"),
                    AccentCyan = Hex("#B388FF"),
                    AccentGreen = Hex("#69F0AE"),
                    AccentRed = Hex("#FF5252"),
                    AccentYellow = Hex("#FFD740"),
                    TextPrimary = Hex("#F0E6FF"),
                    TextSecondary = Hex("#B39DDB")
                },
                ["Green"] = new ThemePalette
                {
                    BgDark = Hex("#0F1F1A"),
                    BgCard = Hex("#132922"),
                    AccentBlue = Hex("#1B4332"),
                    AccentCyan = Hex("#00E676"),
                    AccentGreen = Hex("#69F0AE"),
                    AccentRed = Hex("#FF5252"),
                    AccentYellow = Hex("#FFD600"),
                    TextPrimary = Hex("#E8F5E9"),
                    TextSecondary = Hex("#80CBC4")
                },
                ["Amber"] = new ThemePalette
                {
                    BgDark = Hex("#1F1A12"),
                    BgCard = Hex("#2A2115"),
                    AccentBlue = Hex("#4E342E"),
                    AccentCyan = Hex("#FFB300"),
                    AccentGreen = Hex("#AEEA00"),
                    AccentRed = Hex("#FF6E40"),
                    AccentYellow = Hex("#FFD740"),
                    TextPrimary = Hex("#FFF8E1"),
                    TextSecondary = Hex("#BCAAA4")
                },
                ["Red"] = new ThemePalette
                {
                    BgDark = Hex("#1F1216"),
                    BgCard = Hex("#2A151C"),
                    AccentBlue = Hex("#4A1525"),
                    AccentCyan = Hex("#FF5252"),
                    AccentGreen = Hex("#69F0AE"),
                    AccentRed = Hex("#FF1744"),
                    AccentYellow = Hex("#FFAB40"),
                    TextPrimary = Hex("#FFEBEE"),
                    TextSecondary = Hex("#EF9A9A")
                }
            };

        public static void Apply(string? themeName)
        {
            if (Application.Current?.Resources == null) return;
            if (string.IsNullOrWhiteSpace(themeName) || !Palettes.TryGetValue(themeName, out var p))
                p = Palettes["Cyan"];

            var res = Application.Current.Resources;
            SetColor(res, "BgDark", p.BgDark);
            SetColor(res, "BgCard", p.BgCard);
            SetColor(res, "AccentBlue", p.AccentBlue);
            SetColor(res, "AccentCyan", p.AccentCyan);
            SetColor(res, "AccentGreen", p.AccentGreen);
            SetColor(res, "AccentRed", p.AccentRed);
            SetColor(res, "AccentYellow", p.AccentYellow);
            SetColor(res, "TextPrimary", p.TextPrimary);
            SetColor(res, "TextSecondary", p.TextSecondary);

            SetBrush(res, "BgDarkBrush", p.BgDark);
            SetBrush(res, "BgCardBrush", p.BgCard);
            SetBrush(res, "AccentBlueBrush", p.AccentBlue);
            SetBrush(res, "AccentCyanBrush", p.AccentCyan);
            SetBrush(res, "AccentGreenBrush", p.AccentGreen);
            SetBrush(res, "AccentRedBrush", p.AccentRed);
            SetBrush(res, "AccentYellowBrush", p.AccentYellow);
            SetBrush(res, "TextPrimaryBrush", p.TextPrimary);
            SetBrush(res, "TextSecondaryBrush", p.TextSecondary);
        }

        public static string Normalize(string? themeName)
        {
            if (string.IsNullOrWhiteSpace(themeName)) return "Cyan";
            foreach (var name in ThemeNames)
            {
                if (string.Equals(name, themeName, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            return "Cyan";
        }

        private static void SetColor(ResourceDictionary res, string key, Color color)
        {
            res[key] = color;
        }

        private static void SetBrush(ResourceDictionary res, string key, Color color)
        {
            // New brush instance so DynamicResource consumers refresh
            res[key] = new SolidColorBrush(color);
        }

        private static Color Hex(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex)!;
        }
    }
}
