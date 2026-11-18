using System.Diagnostics;
using Wagterm.Pty;

namespace Wagterm.Pty.Tests;

public sealed class AppUpdateHostTests : IDisposable
{
    private readonly string _appId = "wagterm-update-host-" + Guid.NewGuid().ToString("N");
    private readonly List<Process> _hosts = [];
    private readonly List<Process> _children = [];
    private readonly string _runtime = Path.Combine(Path.GetTempPath(), "wagterm-update-runtime-" + Guid.NewGuid().ToString("N"));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Wagterm.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private Process Start(string appId, string? exe = null, bool managed = false, bool bundled = false)
    {
        exe ??= Path.Combine(RepositoryRoot(), "native", "target", "release", "wagterm-ptyhost.exe");
        Assert.True(File.Exists(exe), "Build the native host before running managed tests");
        string args = (managed ? "--pty-host " : "") + $"--pipe {appId}" + (bundled ? " --conpty bundled" : "");
        var process = Process.Start(new ProcessStartInfo(exe, args)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! })!;
        _hosts.Add(process);
        Assert.True(SpinWait.SpinUntil(() => PtyHostClient.IsRunning(appId), 10_000), "test host did not start");
        return process;
    }

    public void Dispose()
    {
        foreach (var host in _hosts.Concat(_children))
        {
            if (!host.HasExited) { host.Kill(entireProcessTree: true); host.WaitForExit(5000); }
            host.Dispose();
        }
        try { if (Directory.Exists(_runtime)) Directory.Delete(_runtime, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void StopsBothNamespacesAndDirectChildrenBeforeFreshNativeHostsCanStart()
    {
        using var other = PtyHostClient.Connect(StartOther());
        var otherHost = Assert.Single(_hosts);
        var hosts = new List<Process>();
        var childProcesses = new List<Process>();
        try
        {
            foreach (string name in new[] { _appId, _appId + "-rust" })
            {
                hosts.Add(Start(name));
                using var client = PtyHostClient.Connect(name);
                client.Create("child", 80, 24, Path.Combine(Environment.SystemDirectory, "ping.exe"), ["127.0.0.1", "-t"]);
                childProcesses.Add(Process.GetProcessById(Assert.Single(client.List()).ChildPid!.Value));
            }
            int[] oldPids = hosts.Select(host => host.Id).ToArray();
            AppUpdate.StopSessionHostsForUpdate(_appId);
            Assert.All(hosts, host => Assert.True(host.HasExited));
            Assert.All(childProcesses, child => Assert.True(child.WaitForExit(10_000), "host shutdown left its terminal child running"));
            Assert.False(otherHost.HasExited); // Another instance must remain untouched.
            Assert.Single(other.List());
            foreach (string name in new[] { _appId, _appId + "-rust" })
                Assert.DoesNotContain(Start(name).Id, oldPids);
        }
        finally { foreach (var child in childProcesses) child.Dispose(); }
    }

    [Fact]
    public async Task ManagedAndRustBundledHostsReleaseRuntimeFilesWithGrandchildren()
    {
        string managedOutput = Path.Combine(RepositoryRoot(), "src", "Wagterm.Win32", "bin", "x64", "Release", "net10.0-windows", "win-x64");
        Assert.True(File.Exists(Path.Combine(managedOutput, "wagterm.exe")), "Build the managed solution before running host tests");
        Directory.CreateDirectory(Path.Combine(_runtime, "x64"));
        foreach (string file in Directory.EnumerateFiles(managedOutput))
        {
            string ext = Path.GetExtension(file);
            if (ext is ".dll" or ".json" or ".exe" && !Path.GetFileName(file).StartsWith("Agwinterm.", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(_runtime, Path.GetFileName(file)));
        }
        File.Copy(Path.Combine(managedOutput, "x64", "OpenConsole.exe"), Path.Combine(_runtime, "x64", "OpenConsole.exe"));
        File.Copy(Path.Combine(RepositoryRoot(), "native", "target", "release", "wagterm-ptyhost.exe"), Path.Combine(_runtime, "wagterm-ptyhost.exe"));
        var hosts = new[]
        {
            Start(_appId, Path.Combine(_runtime, "wagterm.exe"), managed: true, bundled: true),
            Start(SessionBackends.RustNamespace(_appId), Path.Combine(_runtime, "wagterm-ptyhost.exe"), bundled: true),
        };
        foreach (string name in new[] { _appId, SessionBackends.RustNamespace(_appId) })
        {
            string pidFile = Path.Combine(_runtime, name + ".pid");
            using var client = PtyHostClient.Connect(name);
            string script = """
                $ErrorActionPreference = 'Stop'
                try {
                  $p = Start-Process -FilePath "$env:SystemRoot\System32\ping.exe" -ArgumentList '127.0.0.1','-t' -NoNewWindow -PassThru
                  [IO.File]::WriteAllText($env:WAGTERM_UPDATE_TEST_PID_FILE, [string]$p.Id)
                  $p.WaitForExit()
                } catch {
                  [IO.File]::WriteAllText('ERROR_FILE', ($_ | Out-String))
                  exit 1
                }
                """.Replace("ERROR_FILE", (pidFile + ".error").Replace("'", "''"));
            client.Create("parent", 80, 24,
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))],
                env: new Dictionary<string, string> { ["WAGTERM_UPDATE_TEST_PID_FILE"] = pidFile });
            _children.Add(Process.GetProcessById(Assert.Single(client.List()).ChildPid!.Value));
            if (!SpinWait.SpinUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, 15_000))
            {
                using var attachment = client.Attach("parent", repaint: true);
                var output = new System.Text.StringBuilder(string.Join("\n", attachment.Scrollback));
                using var stopRead = new CancellationTokenSource(2000);
                var bytes = new byte[8192];
                try
                {
                    while (true)
                    {
                        int count = await attachment.Data.ReadAsync(bytes, stopRead.Token);
                        if (count == 0) break;
                        output.Append(System.Text.Encoding.UTF8.GetString(bytes, 0, count));
                    }
                }
                catch (OperationCanceledException) { }
                string scriptError = File.Exists(pidFile + ".error") ? File.ReadAllText(pidFile + ".error") : "";
                Assert.Fail($"Grandchild did not start in {name}, parent exited={attachment.HasExited}, code={attachment.ExitCode}: {output} {scriptError}");
            }
            _children.Add(Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))));
        }
        // Prove the bundled binaries are mapped, so this reproduces the actual install failure.
        Assert.False(CanOverwrite(Path.Combine(_runtime, "conpty.dll")));
        Assert.False(CanOverwrite(Path.Combine(_runtime, "x64", "OpenConsole.exe")));
        var stopStart = new ProcessStartInfo(Path.Combine(_runtime, "wagterm.exe"), "--stop-session-hosts-for-update")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        stopStart.Environment["WAGTERM_UPDATE_HOST_NAMESPACE"] = _appId;
        using var stopping = Process.Start(stopStart)!;
        var errors = stopping.StandardError.ReadToEndAsync();
        try
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await stopping.WaitForExitAsync(stopTimeout.Token);
            Assert.True(stopping.ExitCode == 0, $"Update shutdown failed: {await errors}");
        }
        finally
        {
            if (!stopping.HasExited) { stopping.Kill(); await stopping.WaitForExitAsync(); }
        }
        Assert.All(hosts, host => Assert.True(host.HasExited));
        // Check immediately at the production boundary, before waiting for descendants.
        foreach (string file in Directory.EnumerateFiles(_runtime, "*", SearchOption.AllDirectories))
            Assert.True(CanOverwrite(file), $"Runtime file is still locked after host shutdown: {file}");
        Assert.All(_children, child => Assert.True(child.WaitForExit(10_000), "terminal descendant survived shutdown"));
    }

    private static bool CanOverwrite(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public async Task RuntimeWaitContinuesWhenAFileIsReleased()
    {
        Directory.CreateDirectory(Path.Combine(_runtime, "x64"));
        string file = Path.Combine(_runtime, "x64", "OpenConsole.exe");
        File.WriteAllText(file, "locked runtime");
        using var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = Task.Run(() => AppUpdate.WaitForConsoleRuntimeForUpdate(_runtime, TimeSpan.FromSeconds(5), message => observed.TrySetResult(message)));
        try
        {
            Assert.Contains(file, await observed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(wait.IsCompleted);
        }
        finally { locked.Dispose(); }
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RuntimeWaitTimesOutAndNamesTheLockedFile()
    {
        Directory.CreateDirectory(_runtime);
        string file = Path.Combine(_runtime, "conpty.dll");
        File.WriteAllText(file, "locked runtime");
        using var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var messages = new List<string>();
        var error = Assert.Throws<TimeoutException>(() => AppUpdate.WaitForConsoleRuntimeForUpdate(_runtime, TimeSpan.FromMilliseconds(100), messages.Add));
        Assert.Contains(file, error.Message);
        Assert.Contains(file, Assert.Single(messages));
    }

    private string StartOther()
    {
        string name = _appId + "-other";
        Start(name);
        using var client = PtyHostClient.Connect(name);
        client.Create("unrelated", 80, 24, Path.Combine(Environment.SystemDirectory, "ping.exe"), ["127.0.0.1", "-t"]);
        return name;
    }

    [Fact]
    public void AttachedWindowPreventsHostShutdown()
    {
        var idleHost = Start(_appId);
        var host = Start(_appId + "-rust");
        using var client = PtyHostClient.Connect(_appId + "-rust");
        client.Create("attached", 80, 24, Path.Combine(Environment.SystemDirectory, "ping.exe"), ["127.0.0.1", "-t"]);
        using var attachment = client.Attach("attached").Data;
        var error = Assert.Throws<InvalidOperationException>(() => AppUpdate.StopSessionHostsForUpdate(_appId));
        Assert.Contains("still attached", error.Message);
        Assert.False(idleHost.HasExited);
        Assert.False(host.HasExited);
        Assert.Single(client.List());
    }

    [Fact]
    public void MissingHostsAreHarmless()
        => AppUpdate.StopSessionHostsForUpdate(_appId);
}
