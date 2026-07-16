using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PalAssist.Core
{
    /// <summary>
    /// Checks GitHub Releases for a newer PalAssist build, downloads the zip,
    /// stages the exe, and applies it via a short cmd script after the app exits.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        public const string Owner = "mwtnov240";
        public const string Repo  = "PalAssist";

        private static readonly HttpClient Http = CreateClient();

        private readonly string _stageDir;
        private string? _stagedExePath;
        private UpdateCheckResult? _pending;

        public UpdateService()
        {
            _stageDir = Path.Combine(Path.GetTempPath(), "PalAssistUpdate");
        }

        /// <summary>Currently staged update, if any.</summary>
        public UpdateCheckResult? PendingUpdate => _pending;

        /// <summary>True when a newer exe has been downloaded and is ready to install.</summary>
        public bool IsStaged =>
            _stagedExePath != null && File.Exists(_stagedExePath);

        public static string GetCurrentVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip any +metadata suffix from InformationalVersion
                int plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }

            var ver = asm.GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }

        /// <summary>
        /// Query GitHub for the latest release. Does not download.
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
        {
            string current = GetCurrentVersion();
            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.NoUpdate(current, "No releases found.");
            }

            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string hint = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "GitHub rate limit or access denied."
                    : $"GitHub returned {(int)resp.StatusCode}.";
                return UpdateCheckResult.Fail(current, $"{hint} {Truncate(body, 120)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                return UpdateCheckResult.Fail(current, "Could not parse release info.");

            string remoteTag = release.TagName.Trim();
            string remoteVer = NormalizeVersion(remoteTag);

            if (!TryParseVersion(remoteVer, out var remote) || !TryParseVersion(current, out var local))
                return UpdateCheckResult.Fail(current, $"Could not compare versions ({current} vs {remoteTag}).");

            if (remote <= local)
                return UpdateCheckResult.NoUpdate(current, $"Up to date (v{current}).");

            var asset = PickAsset(release.Assets);
            if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return UpdateCheckResult.Fail(current, "Latest release has no downloadable zip asset.");

            var result = new UpdateCheckResult
            {
                Success = true,
                UpdateAvailable = true,
                CurrentVersion = current,
                LatestVersion = remoteVer,
                TagName = remoteTag,
                ReleaseNotes = release.Body ?? "",
                DownloadUrl = asset.BrowserDownloadUrl,
                AssetName = asset.Name ?? "update.zip",
                AssetSizeBytes = asset.Size,
                Message = $"Update available: v{remoteVer}"
            };

            _pending = result;
            return result;
        }

        /// <summary>
        /// Download the pending release zip and extract PalAssist.exe to a temp stage folder.
        /// </summary>
        public async Task<UpdateCheckResult> DownloadAndStageAsync(
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            if (_pending == null || !_pending.UpdateAvailable || string.IsNullOrWhiteSpace(_pending.DownloadUrl))
                return UpdateCheckResult.Fail(GetCurrentVersion(), "No update is pending. Check first.");

            string current = _pending.CurrentVersion;
            string zipPath = Path.Combine(_stageDir, "download.zip");

            try
            {
                if (Directory.Exists(_stageDir))
                    Directory.Delete(_stageDir, recursive: true);
                Directory.CreateDirectory(_stageDir);

                await DownloadFileAsync(_pending.DownloadUrl!, zipPath, progress, ct).ConfigureAwait(false);

                ZipFile.ExtractToDirectory(zipPath, _stageDir, overwriteFiles: true);

                // Prefer root or nested PalAssist.exe
                string? exe = Directory.EnumerateFiles(_stageDir, "PalAssist.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (exe == null || !File.Exists(exe))
                {
                    _stagedExePath = null;
                    return UpdateCheckResult.Fail(current, "Zip did not contain PalAssist.exe.");
                }

                _stagedExePath = exe;

                // Keep pending metadata; mark staged
                _pending.Message = $"v{_pending.LatestVersion} ready to install";
                _pending.Staged = true;
                return _pending;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _stagedExePath = null;
                return UpdateCheckResult.Fail(current, $"Download failed: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Check for update and, if available, download+stage in one call.
        /// </summary>
        public async Task<UpdateCheckResult> CheckAndDownloadAsync(
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            var check = await CheckForUpdateAsync(ct).ConfigureAwait(false);
            if (!check.Success || !check.UpdateAvailable)
                return check;

            return await DownloadAndStageAsync(progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Launch a helper script that replaces the running exe after this process exits,
        /// then restarts PalAssist. Call Application.Current.Shutdown() after this returns true.
        /// </summary>
        public bool ApplyAndRestart()
        {
            if (!IsStaged || _stagedExePath == null)
                return false;

            string? installExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(installExe) || !File.Exists(installExe))
                return false;

            string installDir = Path.GetDirectoryName(installExe)!;
            int pid = Environment.ProcessId;

            // Script lives in %TEMP% (not inside stage dir) so it can delete the stage folder.
            string scriptPath = Path.Combine(Path.GetTempPath(), $"PalAssist_apply_{pid}.cmd");
            string staged = _stagedExePath;
            string stageDir = _stageDir;

            string script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                ":wait\r\n" +
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                "ping 127.0.0.1 -n 2 >nul\r\n" +
                $"copy /Y \"{staged}\" \"{installExe}\" >nul\r\n" +
                "if errorlevel 1 (\r\n" +
                "  ping 127.0.0.1 -n 2 >nul\r\n" +
                $"  copy /Y \"{staged}\" \"{installExe}\" >nul\r\n" +
                ")\r\n" +
                $"start \"\" \"{installExe}\"\r\n" +
                $"rmdir /S /Q \"{stageDir}\" 2>nul\r\n" +
                "endlocal\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n";

            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C \"{scriptPath}\"",
                WorkingDirectory = installDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            return true;
        }

        public void Dispose()
        {
            // Intentionally leave staged files if an update is ready; apply script cleans them.
        }

        // ── helpers ──────────────────────────────────────────

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            // User-Agent required by GitHub API; do not set Accept globally (breaks binary downloads).
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PalAssist-Updater");
            return client;
        }

        private static async Task DownloadFileAsync(
            string url, string destPath, IProgress<double>? progress, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                readTotal += read;
                if (total is > 0)
                    progress?.Report(Math.Clamp(100.0 * readTotal / total.Value, 0, 100));
            }

            progress?.Report(100);
        }

        private static GitHubAsset? PickAsset(GitHubAsset[]? assets)
        {
            if (assets == null || assets.Length == 0) return null;

            // Prefer self-contained win-x64 zip naming used by our releases
            var preferred = assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("PalAssist", StringComparison.OrdinalIgnoreCase));

            if (preferred != null) return preferred;

            preferred = assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("PalAssist", StringComparison.OrdinalIgnoreCase));

            if (preferred != null) return preferred;

            return assets.FirstOrDefault(a =>
                a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        public static string NormalizeVersion(string tagOrVersion)
        {
            string s = tagOrVersion.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                s = s[1..];
            // Drop pre-release suffix like 1.0.0-beta for Version.Parse base
            int dash = s.IndexOf('-');
            if (dash > 0) s = s[..dash];
            return s;
        }

        public static bool TryParseVersion(string s, out Version version)
        {
            version = new Version(0, 0, 0);
            s = NormalizeVersion(s);
            if (Version.TryParse(s, out var v))
            {
                // Normalize to 3-part for comparison
                version = new Version(
                    Math.Max(v.Major, 0),
                    Math.Max(v.Minor, 0),
                    v.Build >= 0 ? v.Build : 0);
                return true;
            }

            // Handle "1.0" → 1.0.0
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int maj) &&
                int.TryParse(parts[1], out int min))
            {
                int build = 0;
                if (parts.Length >= 3) int.TryParse(parts[2], out build);
                version = new Version(maj, min, build);
                return true;
            }

            return false;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }

    public sealed class UpdateCheckResult
    {
        public bool Success { get; init; }
        public bool UpdateAvailable { get; init; }
        public bool Staged { get; set; }
        public string CurrentVersion { get; init; } = "0.0.0";
        public string LatestVersion { get; init; } = "0.0.0";
        public string TagName { get; init; } = "";
        public string ReleaseNotes { get; init; } = "";
        public string? DownloadUrl { get; init; }
        public string AssetName { get; init; } = "";
        public long AssetSizeBytes { get; init; }
        public string Message { get; set; } = "";

        public static UpdateCheckResult NoUpdate(string current, string message) => new()
        {
            Success = true,
            UpdateAvailable = false,
            CurrentVersion = current,
            LatestVersion = current,
            Message = message
        };

        public static UpdateCheckResult Fail(string current, string message) => new()
        {
            Success = false,
            UpdateAvailable = false,
            CurrentVersion = current,
            Message = message
        };
    }

    // ── GitHub API DTOs ──────────────────────────────────

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
