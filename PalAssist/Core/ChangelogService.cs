using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PalAssist.Core
{
    /// <summary>
    /// Loads and parses the embedded / shipped CHANGELOG.md.
    /// </summary>
    public static class ChangelogService
    {
        private static readonly Regex VersionHeader = new(
            @"^##\s+v?(\d+\.\d+\.\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Full changelog text, or empty if missing.</summary>
        public static string LoadRaw()
        {
            // 1) Next to the executable (Content copy)
            try
            {
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CHANGELOG.md");
                if (File.Exists(beside))
                    return File.ReadAllText(beside);
            }
            catch { /* ignore */ }

            // 2) Embedded resource (if present)
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase))
                    {
                        using var stream = asm.GetManifestResourceStream(name);
                        if (stream == null) continue;
                        using var reader = new StreamReader(stream);
                        return reader.ReadToEnd();
                    }
                }
            }
            catch { /* ignore */ }

            return "";
        }

        /// <summary>
        /// Returns notes for a single version section (body under ## X.Y.Z), or empty.
        /// </summary>
        public static string GetNotesForVersion(string version)
        {
            version = UpdateService.NormalizeVersion(version);
            var sections = ParseSections(LoadRaw());
            return sections.TryGetValue(version, out var body) ? body.Trim() : "";
        }

        /// <summary>
        /// Notes for all versions newer than <paramref name="afterVersion"/> (exclusive),
        /// newest first. If afterVersion is empty, returns current version only when provided.
        /// </summary>
        public static string GetNotesSince(string? afterVersion, string? currentVersion = null)
        {
            var sections = ParseSections(LoadRaw());
            if (sections.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(currentVersion))
                    return $"PalAssist v{UpdateService.NormalizeVersion(currentVersion)}";
                return "No changelog entries found.";
            }

            Version? after = null;
            if (!string.IsNullOrWhiteSpace(afterVersion) &&
                UpdateService.TryParseVersion(afterVersion, out var av))
                after = av;

            var sb = new StringBuilder();
            // Preserve file order (newest-first if author keeps that convention)
            foreach (var kv in sections)
            {
                if (!UpdateService.TryParseVersion(kv.Key, out var ver))
                    continue;
                if (after != null && ver <= after)
                    continue;

                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append("## ").Append(kv.Key).AppendLine().AppendLine();
                sb.Append(kv.Value.Trim());
            }

            if (sb.Length == 0 && !string.IsNullOrWhiteSpace(currentVersion))
            {
                string cur = UpdateService.NormalizeVersion(currentVersion);
                if (sections.TryGetValue(cur, out var body))
                {
                    sb.Append("## ").Append(cur).AppendLine().AppendLine();
                    sb.Append(body.Trim());
                }
                else
                {
                    sb.Append("Updated to v").Append(cur).Append('.');
                }
            }

            return sb.Length > 0 ? sb.ToString() : "No new changelog entries.";
        }

        /// <summary>Full formatted changelog for the About viewer.</summary>
        public static string GetFullChangelog()
        {
            string raw = LoadRaw().Trim();
            return string.IsNullOrEmpty(raw) ? "No changelog available." : raw;
        }

        private static Dictionary<string, string> ParseSections(string raw)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var matches = VersionHeader.Matches(raw);
            for (int i = 0; i < matches.Count; i++)
            {
                string ver = matches[i].Groups[1].Value;
                int start = matches[i].Index + matches[i].Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : raw.Length;
                string body = raw[start..end].Trim();
                if (!result.ContainsKey(ver))
                    result[ver] = body;
            }
            return result;
        }
    }
}
