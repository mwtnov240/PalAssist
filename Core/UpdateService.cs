using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PalAssist.Core
{
    /// <summary>
    /// Checks GitHub Releases for a newer PalAssist build, downloads the .exe
    /// (or zip), stages it, and applies via a short cmd script after the app exits.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        public const string Owner = "mwtnov240";
        public const string Repo  = "PalAssist";

        private static readonly HttpClient Http = CreateClient();

        private readonly string _stageDir;
        private readonly CancellationTokenSource _cts = new();
        private string? _stagedExePath;
        private UpdateCheckResult? _pending;
        private bool _disposed;
        private bool _keepStageForApply;

        public UpdateService()
        {
            _stageDir = Path.Combine(Path.GetTempPath(), "PalAssist2Update");
        }

        /// <summary>Token cancelled when the service is disposed (app exit).</summary>
        public CancellationToken DisposeToken => _cts.Token;

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
                return UpdateCheckResult.Fail(current, "Latest release has no downloadable .exe or .zip asset.");

            var result = new UpdateCheckResult
            {
                Success = true,
                UpdateAvailable = true,
                CurrentVersion = current,
                LatestVersion = remoteVer,
                TagName = remoteTag,
                ReleaseNotes = release.Body ?? "",
                DownloadUrl = asset.BrowserDownloadUrl,
                AssetName = asset.Name ?? "PalAssist.exe",
                AssetSizeBytes = asset.Size,
                ExpectedSha256 = NormalizeDigest(asset.Digest),
                Message = $"Update available: v{remoteVer}"
            };

            _pending = result;
            return result;
        }

        /// <summary>
        /// Check for updates with one retry on transient network/API failures.
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdateWithRetryAsync(
            int maxAttempts = 2,
            CancellationToken ct = default)
        {
            maxAttempts = Math.Clamp(maxAttempts, 1, 4);
            UpdateCheckResult? last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    last = await CheckForUpdateAsync(ct).ConfigureAwait(false);
                    if (last.Success)
                        return last;

                    // Retry only on likely-transient failures
                    bool transient = last.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                        || last.Message.Contains("returned 5", StringComparison.OrdinalIgnoreCase)
                        || last.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                        || last.Message.Contains("network", StringComparison.OrdinalIgnoreCase);
                    if (!transient || attempt == maxAttempts)
                        return last;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    AppLog.Warn("UpdateService.CheckRetry", $"Attempt {attempt} failed: {ex.Message}");
                    last = UpdateCheckResult.Fail(GetCurrentVersion(), ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
            }

            return last ?? UpdateCheckResult.Fail(GetCurrentVersion(), "Update check failed.");
        }

        /// <summary>
        /// Download the pending release asset (.exe preferred, or .zip) and stage PalAssist.exe.
        /// </summary>
        public async Task<UpdateCheckResult> DownloadAndStageAsync(
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            if (_pending == null || !_pending.UpdateAvailable || string.IsNullOrWhiteSpace(_pending.DownloadUrl))
                return UpdateCheckResult.Fail(GetCurrentVersion(), "No update is pending. Check first.");

            string current = _pending.CurrentVersion;
            string assetName = _pending.AssetName ?? "";
            bool isZip = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            string downloadPath = Path.Combine(_stageDir, isZip ? "download.zip" : "PalAssist.exe");

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                var token = linked.Token;

                if (Directory.Exists(_stageDir))
                    TryDeleteDirectory(_stageDir);
                Directory.CreateDirectory(_stageDir);

                await DownloadFileAsync(_pending.DownloadUrl!, downloadPath, progress, token).ConfigureAwait(false);

                // Verify the raw download (size / optional GitHub digest for the asset itself)
                if (!isZip)
                {
                    var rawCheck = VerifyDownloadedFile(
                        downloadPath,
                        expectedSize: _pending.AssetSizeBytes,
                        expectedSha256: _pending.ExpectedSha256,
                        requirePe: true);
                    if (!rawCheck.Ok)
                    {
                        _stagedExePath = null;
                        TryDeleteDirectory(_stageDir);
                        return UpdateCheckResult.Fail(current, "Download verification failed: " + rawCheck.Error);
                    }
                }
                else if (_pending.AssetSizeBytes > 0)
                {
                    long len = new FileInfo(downloadPath).Length;
                    if (Math.Abs(len - _pending.AssetSizeBytes) > 64)
                    {
                        // Zip size mismatch — soft warn; still extract and verify the inner exe
                        AppLog.Warn("UpdateService.Download",
                            $"Zip size mismatch: got {len}, expected {_pending.AssetSizeBytes}");
                    }
                }

                string? exe;
                if (isZip)
                {
                    ZipFile.ExtractToDirectory(downloadPath, _stageDir, overwriteFiles: true);
                    try { File.Delete(downloadPath); } catch { /* ignore */ }

                    exe = Directory.EnumerateFiles(_stageDir, "PalAssist.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();

                    if (exe == null || !File.Exists(exe))
                    {
                        _stagedExePath = null;
                        TryDeleteDirectory(_stageDir);
                        return UpdateCheckResult.Fail(current, "Zip did not contain PalAssist.exe.");
                    }

                    // Inner exe: PE + min size (GitHub digest was for the zip, not the exe)
                    var inner = VerifyDownloadedFile(exe, expectedSize: 0, expectedSha256: null, requirePe: true);
                    if (!inner.Ok)
                    {
                        _stagedExePath = null;
                        TryDeleteDirectory(_stageDir);
                        return UpdateCheckResult.Fail(current, "Extracted exe invalid: " + inner.Error);
                    }
                }
                else
                {
                    exe = downloadPath;
                }

                // Final gate before staging
                var finalCheck = VerifyDownloadedFile(exe, expectedSize: isZip ? 0 : _pending.AssetSizeBytes,
                    expectedSha256: isZip ? null : _pending.ExpectedSha256, requirePe: true);
                if (!finalCheck.Ok)
                {
                    _stagedExePath = null;
                    TryDeleteDirectory(_stageDir);
                    return UpdateCheckResult.Fail(current, "Staged file verification failed: " + finalCheck.Error);
                }

                _stagedExePath = exe;
                _keepStageForApply = true;
                try
                {
                    File.WriteAllText(exe + ".sha256", ComputeSha256Hex(exe));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("UpdateService.Download", "Could not write sha256 sidecar: " + ex.Message);
                }

                _pending.Message = $"v{_pending.LatestVersion} ready to install (verified)";
                _pending.Staged = true;
                AppLog.Info("UpdateService", $"Staged update v{_pending.LatestVersion} ({finalCheck.Sha256?[..12]}…)");
                return _pending;
            }
            catch (OperationCanceledException)
            {
                _stagedExePath = null;
                _keepStageForApply = false;
                TryDeleteDirectory(_stageDir);
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error("UpdateService.Download", ex.Message, ex);
                _stagedExePath = null;
                _keepStageForApply = false;
                TryDeleteDirectory(_stageDir);
                return UpdateCheckResult.Fail(current, $"Download failed: {ex.Message}");
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

            // Re-verify immediately before install — catches corruption after staging
            string? expectedHash = null;
            try
            {
                string side = _stagedExePath + ".sha256";
                if (File.Exists(side))
                    expectedHash = File.ReadAllText(side).Trim();
            }
            catch { /* ignore */ }

            if (string.IsNullOrEmpty(expectedHash) && !string.IsNullOrEmpty(_pending?.ExpectedSha256))
                expectedHash = _pending!.ExpectedSha256;

            var recheck = VerifyDownloadedFile(
                _stagedExePath,
                expectedSize: 0,
                expectedSha256: expectedHash,
                requirePe: true);
            if (!recheck.Ok)
            {
                AppLog.Error("UpdateService.Apply", "Pre-install verify failed: " + recheck.Error);
                _stagedExePath = null;
                _keepStageForApply = false;
                TryDeleteDirectory(_stageDir);
                return false;
            }

            string? installExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(installExe) || !File.Exists(installExe))
                return false;

            string installDir = Path.GetDirectoryName(installExe)!;
            int pid = Environment.ProcessId;

            // Marker so the new process force-releases keys on first launch
            string postUpdateMarker = Path.Combine(installDir, "PalAssist2.postupdate");

            string scriptPath = Path.Combine(Path.GetTempPath(), $"PalAssist2_apply_{pid}.cmd");
            string staged = _stagedExePath;
            string stageDir = _stageDir;

            // Wait for process exit, retry copy up to 5 times, write post-update marker, restart, clean
            string script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                "set RETRIES=0\r\n" +
                ":wait\r\n" +
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                "ping 127.0.0.1 -n 2 >nul\r\n" +
                ":copytry\r\n" +
                $"copy /Y \"{staged}\" \"{installExe}\" >nul\r\n" +
                "if errorlevel 1 (\r\n" +
                "  set /a RETRIES+=1\r\n" +
                "  if %RETRIES% GEQ 5 goto fail\r\n" +
                "  ping 127.0.0.1 -n 2 >nul\r\n" +
                "  goto copytry\r\n" +
                ")\r\n" +
                $"echo postupdate>\"{postUpdateMarker}\"\r\n" +
                $"start \"\" \"{installExe}\"\r\n" +
                "goto cleanup\r\n" +
                ":fail\r\n" +
                "echo PalAssist update copy failed.>>\"%TEMP%\\PalAssist2_update_fail.log\"\r\n" +
                ":cleanup\r\n" +
                $"rmdir /S /Q \"{stageDir}\" 2>nul\r\n" +
                "endlocal\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n";

            File.WriteAllText(scriptPath, script);
            _keepStageForApply = true;

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
            AppLog.Info("UpdateService", "Apply script launched");
            return true;
        }

        /// <summary>
        /// Marker file written by the apply script next to the installed exe.
        /// </summary>
        public static string PostUpdateMarkerPath
        {
            get
            {
                string? dir = Path.GetDirectoryName(
                    Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName);
                return Path.Combine(dir ?? AppDomain.CurrentDomain.BaseDirectory, "PalAssist2.postupdate");
            }
        }

        public static bool ConsumePostUpdateMarker()
        {
            try
            {
                string path = PostUpdateMarkerPath;
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }

            // Leave staged files only if apply script was launched
            if (!_keepStageForApply || !IsStaged)
                TryDeleteDirectory(_stageDir);
        }

        // ── helpers ──────────────────────────────────────────

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("UpdateService.Cleanup", "Could not delete stage: " + ex.Message);
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PalAssist2-Updater/2.0.1");
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

        private readonly struct VerifyResult
        {
            public bool Ok { get; init; }
            public string Error { get; init; }
            public string? Sha256 { get; init; }
            public static VerifyResult Fail(string error) => new() { Ok = false, Error = error };
            public static VerifyResult Pass(string? sha) => new() { Ok = true, Error = "", Sha256 = sha };
        }

        /// <summary>
        /// Validates a downloaded/staged binary: exists, min size, optional GitHub size,
        /// optional SHA-256 (from API digest), and PE (MZ) header for .exe.
        /// Authenticode is not required (releases may be unsigned); size+hash+PE are the bar.
        /// </summary>
        private static VerifyResult VerifyDownloadedFile(
            string path,
            long expectedSize,
            string? expectedSha256,
            bool requirePe)
        {
            try
            {
                if (!File.Exists(path))
                    return VerifyResult.Fail("file missing");

                var fi = new FileInfo(path);
                if (fi.Length < 64 * 1024)
                    return VerifyResult.Fail($"file too small ({fi.Length} bytes)");

                // GitHub asset size — allow tiny drift only if CDN padding; exact when known
                if (expectedSize > 1024 && Math.Abs(fi.Length - expectedSize) > 0)
                    return VerifyResult.Fail($"size mismatch (got {fi.Length}, expected {expectedSize})");

                if (requirePe && !LooksLikePeExecutable(path))
                    return VerifyResult.Fail("not a valid Windows PE executable");

                string sha = ComputeSha256Hex(path);
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    if (!string.Equals(sha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        return VerifyResult.Fail("SHA-256 mismatch (file may be corrupt or tampered)");
                }

                return VerifyResult.Pass(sha);
            }
            catch (Exception ex)
            {
                return VerifyResult.Fail(ex.Message);
            }
        }

        private static bool LooksLikePeExecutable(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                if (fs.Length < 0x40) return false;
                // MZ
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') return false;
                fs.Seek(0x3C, SeekOrigin.Begin);
                Span<byte> peOffBytes = stackalloc byte[4];
                if (fs.Read(peOffBytes) != 4) return false;
                int peOff = BitConverter.ToInt32(peOffBytes);
                if (peOff <= 0 || peOff + 4 > fs.Length) return false;
                fs.Seek(peOff, SeekOrigin.Begin);
                // PE\0\0
                return fs.ReadByte() == 'P' && fs.ReadByte() == 'E'
                       && fs.ReadByte() == 0 && fs.ReadByte() == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeSha256Hex(string path)
        {
            using var fs = File.OpenRead(path);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>GitHub digest is often "sha256:HEX" — return lowercase hex or null.</summary>
        private static string? NormalizeDigest(string? digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return null;
            string s = digest.Trim();
            const string prefix = "sha256:";
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s = s[prefix.Length..];
            s = s.Trim().ToLowerInvariant();
            // Must look like hex
            if (s.Length != 64) return null;
            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return null;
            }
            return s;
        }

        private static GitHubAsset? PickAsset(GitHubAsset[]? assets)
        {
            if (assets == null || assets.Length == 0) return null;

            // 1) Exact PalAssist.exe (primary: direct download, no unzip)
            var preferred = assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.Equals("PalAssist.exe", StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;

            // 2) Any PalAssist*.exe
            preferred = assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("PalAssist", StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;

            // 3) Self-contained win-x64 zip
            preferred = assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("PalAssist", StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;

            // 4) Any PalAssist zip
            preferred = assets.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("PalAssist", StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;

            // 5) Fallback: any .exe then any .zip
            preferred = assets.FirstOrDefault(a =>
                a.Name != null && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
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
        /// <summary>Optional GitHub release asset SHA-256 (hex), when API provides digest.</summary>
        public string? ExpectedSha256 { get; init; }
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

        /// <summary>GitHub asset integrity digest, e.g. "sha256:…".</summary>
        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
