using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wagterm.Pty;

/// <summary>Which kind of wagterm install is running — decides how (or whether) self-update applies.</summary>
public enum UpdateChannel
{
    /// <summary>Inno per-user install under %LOCALAPPDATA%\Programs\wagterm → silent setup.exe re-run.</summary>
    Installed,
    /// <summary>A bare portable exe anywhere else → rename-swap the exe in place.</summary>
    Portable,
    /// <summary>scoop / chocolatey layout → hands off; the package manager owns the files.</summary>
    PackageManager,
}

/// <summary>One downloadable file of a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>A GitHub release: the version its tag names plus its assets.</summary>
public sealed record ReleaseInfo(string Version, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// wagterm self-update: channel detection, release lookup (GitHub releases API — the same source
/// every install flavor ships from), SHA-256-verified downloads (release assets carry a digest; we
/// have no Authenticode cert, so the digest check is the integrity gate), and the tiny apply-helper
/// script that swaps binaries AFTER the app has exited. Applying is never silent-background: the
/// user triggers it, the app saves state, exits, and relaunches restored.
/// </summary>
public static class AppUpdate
{
    public const string DefaultApiUrl = "https://api.github.com/repos/msize/wagterm/releases/latest";

    /// <summary>Test seam: a local FILE PATH here (instead of an URL) is read as the API response,
    /// and asset "URLs" that are local paths are copied instead of downloaded — lets E2E runs
    /// exercise the whole pipeline hermetically.</summary>
    public const string ApiEnvVar = "WAGTERM_UPDATE_API";

    /// <summary>Classify the running install from its own exe path. <paramref name="localAppData"/>
    /// is a parameter for testability (%LOCALAPPDATA% in production).</summary>
    public static UpdateChannel DetectChannel(string exePath, string localAppData)
    {
        if (exePath.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase) ||
            exePath.Contains(@"\chocolatey\", StringComparison.OrdinalIgnoreCase) ||
            exePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return UpdateChannel.PackageManager;
        string installDir = Path.Combine(localAppData, "Programs", "wagterm") + Path.DirectorySeparatorChar;
        return exePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.Installed : UpdateChannel.Portable;
    }

    /// <summary>Parse a GitHub /releases/latest document into version + assets (digest "sha256:hex"
    /// → hex). Null on any shape mismatch — an update must never proceed on a guess.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? ver = root.TryGetProperty("tag_name", out var t) ? ClaudeUpdate.ParseVersion(t.GetString()) : null;
            if (ver is null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<ReleaseAsset>();
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                string? dig = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (dig is not null && dig.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) dig = dig[7..];
                else dig = null;   // unknown digest algorithm → treat as absent (fail closed later)
                if (name is not null && url is not null) list.Add(new ReleaseAsset(name, url, dig));
            }
            return new ReleaseInfo(ver, list);
        }
        catch { return null; }
    }

    /// <summary>The release asset a channel updates from: the setup exe for installed copies, the
    /// portable exe otherwise. Null when the release carries no such asset (or for package managers,
    /// which never self-update).</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, UpdateChannel channel) => channel switch
    {
        UpdateChannel.Installed => release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("wagterm-setup-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
        UpdateChannel.Portable => release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("wagterm-portable-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
        _ => null,
    };

    /// <summary>Fetch and parse the latest release, or null on any failure (offline stays silent).
    /// Honors the <see cref="ApiEnvVar"/> test seam (URL override, or a local JSON file path).</summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        try
        {
            string api = Environment.GetEnvironmentVariable(ApiEnvVar) is { Length: > 0 } o ? o : DefaultApiUrl;
            string json;
            if (!api.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(api))
                json = await File.ReadAllTextAsync(api).ConfigureAwait(false);
            else
            {
                using var http = NewHttp();
                json = await http.GetStringAsync(api).ConfigureAwait(false);
            }
            return ParseRelease(json);
        }
        catch { return null; }
    }

    /// <summary>Download an asset to <paramref name="destPath"/> and verify its SHA-256 against the
    /// release digest. FAIL-CLOSED: no digest, digest mismatch, or any IO error → false and the file
    /// is deleted. Local-path "URLs" are copied (test seam).</summary>
    public static async Task<bool> DownloadVerifiedAsync(ReleaseAsset asset, string destPath)
    {
        if (string.IsNullOrEmpty(asset.Sha256)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            if (!asset.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(asset.Url))
                File.Copy(asset.Url, destPath, overwrite: true);
            else
            {
                using var http = NewHttp();
                await using var src = await http.GetStreamAsync(asset.Url).ConfigureAwait(false);
                await using var dst = File.Create(destPath);
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }
            string actual;
            await using (var f = File.OpenRead(destPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(f).ConfigureAwait(false));
            if (actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
            File.Delete(destPath);
            return false;
        }
        catch
        {
            try { File.Delete(destPath); } catch { }
            return false;
        }
    }

    /// <summary>Stop both host backends in this instance, after its UI saved state and exited.
    /// Shutdown closes hosted sessions. A host that does not exit prevents installation.</summary>
    public static void StopSessionHostsForUpdate(string appId)
    {
        var clients = new List<PtyHostClient>();
        try
        {
            // Check both namespaces before closing any sessions.
            foreach (string name in new[] { appId, SessionBackends.RustNamespace(appId) })
            {
                if (!PtyHostClient.IsRunning(name)) continue;
                var client = PtyHostClient.Connect(name);
                clients.Add(client);
                if (client.List().Any(session => session.Attached))
                    throw new InvalidOperationException("Another WagTerm window is still attached to the terminal host");
            }
            foreach (var client in clients)
            {
                using var host = Process.GetProcessById(checked((int)client.HostPid));
                _ = host.SafeHandle; // Hold the original process handle across shutdown.
                if (client.List().Any(session => session.Attached))
                    throw new InvalidOperationException("Another WagTerm window attached while preparing the update");
                client.Shutdown();
                if (!host.WaitForExit(30_000))
                    throw new TimeoutException($"Terminal host {host.Id} did not exit");
            }
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    /// <summary>OpenConsole.exe can outlive its owning host. Wait for the bundled console runtime
    /// to become writable before setup starts. The caller is itself the managed app, so its own
    /// exe and assemblies are excluded; the PowerShell parent waits for that caller to exit.</summary>
    public static void WaitForConsoleRuntimeForUpdate(string directory, TimeSpan timeout, Action<string>? log = null)
    {
        string[] files = [Path.Combine(directory, "conpty.dll"), Path.Combine(directory, "x64", "OpenConsole.exe"),
            Path.Combine(directory, "wagterm-ptyhost.exe")];
        var elapsed = Stopwatch.StartNew();
        string? lastMessage = null;
        while (true)
        {
            var busy = new List<string>();
            foreach (string file in files)
            {
                if (!File.Exists(file)) continue;
                try { using var probe = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { busy.Add(file); }
                catch (UnauthorizedAccessException) { busy.Add(file); }
            }
            if (busy.Count == 0) return;
            string message = "Console runtime files still in use or not writable: " + string.Join(", ", busy);
            if (message != lastMessage) { log?.Invoke(message); lastMessage = message; }
            if (elapsed.Elapsed >= timeout) throw new TimeoutException(message);
            Thread.Sleep(100);
        }
    }

    /// <summary>Script embedded in the running build and written beside its download. Waits for
    /// that UI to exit, stops its terminal hosts, then runs setup or swaps the portable exe with
    /// rollback on swap failure. Checks setup's exit code and the resulting version, writes a JSON
    /// result, and relaunches the same exe unless the UI never exited. Required LogPath persists
    /// outside payload cleanup. A portable .old exe is removed on the next startup.</summary>
    public static string HelperScript => """
        param([int]$ProcId, [string]$Channel, [string]$Payload, [string]$Exe,
              [string]$ExpectedVersion, [Parameter(Mandatory=$true)][string]$LogPath, [string]$ResultPath)
        $ErrorActionPreference = 'Stop'
        function Log([string]$m) { try { Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value ("{0:yyyy-MM-dd HH:mm:ss.fff} {1}" -f (Get-Date), $m) } catch { } }
        function App-Version([string]$path) { [regex]::Match([Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion, '\d+\.\d+\.\d+').Value }
        $result = @{ success = $false; version = $ExpectedVersion; error = ''; logPath = $LogPath }
        $relaunch = $false
        try {
        $expectedCore = [regex]::Match($ExpectedVersion, '^\d+\.\d+\.\d+').Value
        if ($ExpectedVersion -and -not $expectedCore) { throw "invalid expected version: $ExpectedVersion" }
        Log "wait pid=$ProcId channel=$Channel"
        try { Wait-Process -Id $ProcId -Timeout 120 -ErrorAction SilentlyContinue } catch { }
        if (Get-Process -Id $ProcId -ErrorAction SilentlyContinue) { throw 'app never exited' }
        $relaunch = $true
        Start-Sleep -Milliseconds 500
        if ($Channel -eq 'installed') {
          # Update the running layout, even if uninstall registration points to another copy.
          $installDir = [IO.Path]::GetFullPath((Split-Path -Parent $Exe)).TrimEnd([IO.Path]::DirectorySeparatorChar)
          if ($installDir -eq [IO.Path]::GetPathRoot($installDir).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'refusing a drive-root install directory' }
        } elseif ($ExpectedVersion) {
          $payloadVersion = App-Version $Payload
          if ($payloadVersion -ne $expectedCore) { throw "expected portable WagTerm $ExpectedVersion, found '$payloadVersion'" }
        }
        Log 'app exited; stopping terminal hosts'
        $hostLog = $LogPath + '.hosts.log'
        $stop = Start-Process $Exe -ArgumentList '--stop-session-hosts-for-update' -Wait -PassThru -WindowStyle Hidden -RedirectStandardError $hostLog
        if ($stop.ExitCode -ne 0) { throw "terminal hosts could not stop (exit code $($stop.ExitCode)). See $hostLog" }
        Log 'terminal hosts stopped; applying'
        if ($Channel -eq 'installed') {
          $setupLog = $LogPath + '.setup.log'
          $setupArgs = @('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS',
                         '/RESTARTEXITCODE=3010', ('/LOG="{0}"' -f $setupLog), ('/DIR="{0}"' -f $installDir))
          $setup = Start-Process $Payload -ArgumentList $setupArgs -Wait -PassThru -WindowStyle Hidden
          Log "setup exit code: $($setup.ExitCode); setup log: $setupLog"
          if ($setup.ExitCode -ne 0) { throw "setup failed with exit code $($setup.ExitCode). See $setupLog" }
          Log 'setup finished successfully'
        } else {
          $old = $Exe + '.old'
          Remove-Item $old -Force -ErrorAction SilentlyContinue
          $moved = $false
          for ($i = 0; $i -lt 20 -and -not $moved; $i++) {   # straggler/AV locks: retry ~10s
            try { Move-Item $Exe $old -Force -ErrorAction Stop; $moved = $true } catch { Log ("rename-aside failed: " + $_.Exception.Message); Start-Sleep -Milliseconds 500 }
          }
          if (-not $moved) { throw 'exe still locked' }
          try { Move-Item $Payload $Exe -Force -ErrorAction Stop; Log 'swap done' }
          catch { Log ("swap-in failed, rolling back: " + $_.Exception.Message); Move-Item $old $Exe -Force; throw }
        }
        if ($ExpectedVersion) {
          $actual = App-Version $Exe
          if ($actual -ne $expectedCore) { throw "expected WagTerm $ExpectedVersion after updating, found '$actual'" }
        }
        $result.success = $true
        } catch {
          $result.error = $_.Exception.Message
          Log ("FAILED: " + $result.error)
        }
        if ($ResultPath) {
          try { [IO.File]::WriteAllText($ResultPath, ($result | ConvertTo-Json -Compress)) }
          catch { Log ("cannot save update result: " + $_.Exception.Message) }
        }
        if ($relaunch) {
          [Environment]::SetEnvironmentVariable('WAGTERM_UPDATE_HOST_NAMESPACE', $null, 'Process')
          try { Start-Process $Exe; Log 'relaunched' }
          catch { Log ("relaunch failed: " + $_.Exception.Message); exit 1 }
        }
        if (-not $result.success) { exit 1 }
        """;

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("wagterm");   // GitHub API requires a UA
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
