using System.Diagnostics;
using Wagterm.Pty;
using static Wagterm.Win32.Win32;

namespace Wagterm.Win32;

// wagterm self-update ("Update wagterm" palette action / `wagtermctl app update`):
// check GitHub releases → download the channel's asset → verify SHA-256 → confirm → save state → exit all
// windows gracefully → stop terminal hosts → install → relaunch with fresh terminal sessions.
// Package-manager installs (scoop/choco) are never self-updated — we only point at the manager.
internal partial class Program
{
    /// <summary>The running exe (Environment.ProcessPath — Assembly.Location is empty in the
    /// single-file portable build).</summary>
    private static string AppExePath => Environment.ProcessPath ?? "";

    /// <summary>Version to compare against releases: the stamped InformationalVersion ("dev" in
    /// unstamped builds, which never triggers). WAGTERM_VERSION_OVERRIDE is a test seam.</summary>
    private static string AppCurrentVersion()
        => Environment.GetEnvironmentVariable("WAGTERM_VERSION_OVERRIDE") is { Length: > 0 } o
            ? o : _termProgramVersion;

    private static UpdateChannel AppChannel()
        => AppUpdate.DetectChannel(AppExePath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string UpdatesDir => Path.Combine(AppDir, "updates");
    private static string UpdateResultPath => Path.Combine(AppDir, "update-result.json");
    private static int _appUpdateResultShown;

    private void ShowAppUpdateResult()
    {
        if (!File.Exists(UpdateResultPath) || Interlocked.Exchange(ref _appUpdateResultShown, 1) != 0) return;
        bool success = false;
        bool parsed = false;
        string error = "Update result could not be read. See the diagnostic logs.";
        string log = Path.Combine(AppDir, "update-logs");
        string version = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(UpdateResultPath));
            var result = doc.RootElement;
            if (result.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new System.Text.Json.JsonException("Expected an update result object");
            success = result.TryGetProperty("success", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
            string Text(string key, string fallback) => result.TryGetProperty(key, out var field)
                && field.ValueKind == System.Text.Json.JsonValueKind.String && field.GetString() is { Length: > 0 } text ? text : fallback;
            error = Text("error", "The update did not complete.");
            log = Text("logPath", log);
            version = Text("version", "");
            parsed = true;
        }
        catch
        {
            success = false;
            try
            {
                string logDir = Path.Combine(AppDir, "update-logs");
                Directory.CreateDirectory(logDir);
                File.Move(UpdateResultPath, Path.Combine(logDir, $"invalid-result-{Guid.NewGuid():N}.json"));
            }
            catch { } // Still show the failure if diagnostics could not be archived.
        }
        if (parsed)
            try { File.Delete(UpdateResultPath); } catch { } // Cleanup must not change a successful result.
        if (!success)
            MessageBoxW(_hwnd, "WagTerm could not update" + (version.Length > 0 ? " to " + version : "") +
                ".\n\n" + error + "\n\nDiagnostic log: " + log, "WagTerm Update", MB_ICONERROR);
    }

    /// <summary>Startup housekeeping: drop the ".old" exe a portable swap left behind and any
    /// downloaded payloads from previous updates. Best-effort — a still-locked file waits for the
    /// next start.</summary>
    private static void CleanupUpdateLeftovers()
    {
        try { if (AppExePath.Length > 0 && File.Exists(AppExePath + ".old")) File.Delete(AppExePath + ".old"); } catch { }
        try { if (Directory.Exists(UpdatesDir)) Directory.Delete(UpdatesDir, recursive: true); } catch { }
    }

    /// <summary>One background check ("be aware when a new wagterm ships"): GitHub latest release
    /// vs the running version. New version → one toast per version + the palette hint. The package-
    /// manager toast points at the manager instead of the palette action.</summary>
    private async Task CheckAppUpdateOnce()
    {
        string cur = AppCurrentVersion();
        var rel = await AppUpdate.FetchLatestAsync();
        if (rel is null) return;                                   // offline stays silent
        if (!ClaudeUpdate.IsNewer(rel.Version, cur)) { _appLatest = null; return; }
        bool fresh = !string.Equals(_appLatest, rel.Version, StringComparison.Ordinal);
        _appLatest = rel.Version;
        if (!fresh) return;
        string msg = AppChannel() == UpdateChannel.PackageManager
            ? $"WagTerm {rel.Version} is out — update via your package manager (e.g. scoop update wagterm)"
            : $"WagTerm {rel.Version} is out (you have {cur}) — palette → Update WagTerm";
        Post(() => ShowToast(msg, 6000));
    }

    /// <summary>The self-update workflow. Fail-closed at every step (no digest / bad digest / missing
    /// asset → abort with a toast, nothing applied); after confirmation the app exits and the helper
    /// stops terminal hosts, swaps binaries and relaunches with fresh terminal sessions.</summary>
    private string UpdateWagterm()
    {
        var channel = AppChannel();
        if (channel == UpdateChannel.PackageManager)
            return "this WagTerm is managed by a package manager — update with scoop/choco";
        if (AppExePath.Length == 0) return "cannot resolve the running exe path";
        if (_appUpdating) return "WagTerm update already running";
        _appUpdating = true;
        _ = Task.Run(async () =>
        {
            void Toast(string m) => Post(() => ShowToast(m, 5000));
            bool applying = false;
            try
            {
                string cur = AppCurrentVersion();
                var rel = await AppUpdate.FetchLatestAsync();
            if (rel is null) { Toast("WagTerm update check failed (offline or rate-limited) — try again later"); return; }
                if (!ClaudeUpdate.IsNewer(rel.Version, cur))
            { _appLatest = null; Toast($"WagTerm {cur} is already the latest"); return; }
                var asset = AppUpdate.PickAsset(rel, channel);
                if (asset is null) { Toast($"release {rel.Version} has no {(channel == UpdateChannel.Installed ? "setup" : "portable")} asset"); return; }
                if (string.IsNullOrEmpty(asset.Sha256)) { Toast("release asset carries no SHA-256 digest — refusing an unverifiable update"); return; }
            Toast($"downloading WagTerm {rel.Version}…");
                string payload = Path.Combine(UpdatesDir, asset.Name);
                if (!await AppUpdate.DownloadVerifiedAsync(asset, payload))
                { Toast("download failed SHA-256 verification — update aborted"); return; }

                string helper = Path.Combine(UpdatesDir, "apply-update.ps1");
                await File.WriteAllTextAsync(helper, AppUpdate.HelperScript);
                string logDir = Path.Combine(AppDir, "update-logs");
                Directory.CreateDirectory(logDir);
                string log = Path.Combine(logDir, $"update-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
                applying = true; // UI callback owns resetting the flag on cancellation or launch failure.
                Post(() =>
                {
                    if (MessageBoxW(_hwnd,
                        $"Update WagTerm {cur} to {rel.Version}?\n\n" +
                        "WagTerm and all terminal sessions will restart. Running commands and agents will be stopped.\n\n" +
                        "Your windows and tabs will reopen after the update.",
                        "Update WagTerm", MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2) != IDYES)
                    { _appUpdating = false; return; }
                    ShowToast($"WagTerm {cur} → {rel.Version} — restarting…", 4000);
                    var psi = new ProcessStartInfo("powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\" " +
                        $"-ProcId {Environment.ProcessId} -Channel {(channel == UpdateChannel.Installed ? "installed" : "portable")} " +
                        $"-Payload \"{payload}\" -Exe \"{AppExePath}\" -ExpectedVersion \"{rel.Version}\" " +
                        $"-LogPath \"{log}\" -ResultPath \"{UpdateResultPath}\"")
                    { UseShellExecute = false, CreateNoWindow = true };
                    psi.Environment["WAGTERM_UPDATE_HOST_NAMESPACE"] = _argPipe ?? _appId;
                    try { Process.Start(psi); } catch (Exception ex) { ShowToast("failed to start the update helper: " + ex.Message, 6000); _appUpdating = false; return; }
                    QuitAllWindowsForUpdate();
                });
            }
            catch (Exception ex) { Toast("WagTerm update failed: " + ex.Message); }
            finally { if (!applying) _appUpdating = false; }
        });
        return "checking/downloading update; confirmation required in the WagTerm window";
    }

    /// <summary>Close every window of this process gracefully (each WM_DESTROY saves its tree; the
    /// last one quits). <see cref="_updateQuitting"/> keeps multi-window state marked open so ALL
    /// windows come back after the relaunch — a normal explicit close marks a window non-reopening.</summary>
    private static void QuitAllWindowsForUpdate()
    {
        _updateQuitting = true;
        List<Program> wins;
        lock (_windowIndex) wins = _byId.Values.ToList();
        foreach (var w in wins) w.Post(() => DestroyWindow(w._hwnd));
    }
}
