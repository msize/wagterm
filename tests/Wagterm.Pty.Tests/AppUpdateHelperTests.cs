using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wagterm.Pty;

namespace Wagterm.Pty.Tests;

public sealed class AppUpdateHelperTests : IDisposable
{
    private const string HostNamespace = "isolated-test-namespace";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wagterm update-" + Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_dir, "wagterm");
    private string Exe => Path.Combine(Install, "wagterm.exe");
    private string Result => Path.Combine(_dir, "result.json");
    private string Log => Path.Combine(_dir, "update.log");
    private string Pe => Path.Combine(_dir, "new.exe");
    private string PeVersion => Regex.Match(FileVersionInfo.GetVersionInfo(Pe).ProductVersion ?? "", @"\d+\.\d+\.\d+").Value;

    public AppUpdateHelperTests()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Exe, "old app");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Pe);
        File.WriteAllText(Path.Combine(_dir, "helper.ps1"), AppUpdate.HelperScript);
        File.WriteAllText(Path.Combine(_dir, "wrapper.ps1"), """
            function Wait-Process { param($Id, $Timeout, $ErrorAction) }
            function Get-Process { param($Id, $ErrorAction); if ($env:TEST_ALIVE -eq '1') { 'alive' } }
            function Start-Sleep { param($Milliseconds) }
            function Start-Process {
              param($FilePath, $ArgumentList, [switch]$Wait, [switch]$PassThru, $WindowStyle, $RedirectStandardError)
              if ($ArgumentList -contains '--stop-session-hosts-for-update') {
                [IO.File]::WriteAllText((Join-Path $env:TEST_ROOT 'stop-call.json'),
                  (@{ exe = $FilePath; ns = $env:WAGTERM_UPDATE_HOST_NAMESPACE } | ConvertTo-Json -Compress))
                if ($env:TEST_STOP_EXIT -eq '0') {
                  [IO.File]::WriteAllText((Join-Path $env:TEST_ROOT 'hosts-stopped'), 'yes')
                }
                [pscustomobject]@{ ExitCode = [int]$env:TEST_STOP_EXIT }
              } elseif ($FilePath -eq $env:TEST_EXE) {
                [IO.File]::WriteAllText((Join-Path $env:TEST_ROOT 'relaunched'), 'yes')
                [IO.File]::WriteAllText((Join-Path $env:TEST_ROOT 'relaunch-namespace'), [string]$env:WAGTERM_UPDATE_HOST_NAMESPACE)
              } else {
                if (-not (Test-Path (Join-Path $env:TEST_ROOT 'hosts-stopped'))) { throw 'setup started before terminal hosts stopped' }
                [IO.File]::WriteAllText((Join-Path $env:TEST_ROOT 'setup-args.json'), (ConvertTo-Json -InputObject @($ArgumentList)))
                if ($env:TEST_SETUP_EXIT -eq '0') {
                  Copy-Item -LiteralPath (Join-Path $env:TEST_ROOT 'new.exe') -Destination $env:TEST_EXE -Force
                }
                [pscustomobject]@{ ExitCode = [int]$env:TEST_SETUP_EXIT }
              }
            }
            & (Join-Path $env:TEST_ROOT 'helper.ps1') -ProcId 42 -Channel $env:TEST_CHANNEL `
              -Payload (Join-Path $env:TEST_ROOT 'payload.exe') -Exe $env:TEST_EXE `
              -LogPath (Join-Path $env:TEST_ROOT 'update.log') -ResultPath (Join-Path $env:TEST_ROOT 'result.json') `
              -ExpectedVersion $env:TEST_VERSION
            exit $LASTEXITCODE
            """);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (int ExitCode, JsonElement Result) Run(int setupExit = 0, bool alive = false,
        string channel = "installed", string version = "", int stopExit = 0)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(_dir, "wrapper.ps1") })
            start.ArgumentList.Add(arg);
        start.Environment["TEST_ROOT"] = _dir;
        start.Environment["TEST_EXE"] = Exe;
        start.Environment["TEST_SETUP_EXIT"] = setupExit.ToString();
        start.Environment["TEST_STOP_EXIT"] = stopExit.ToString();
        start.Environment["TEST_ALIVE"] = alive ? "1" : "0";
        start.Environment["TEST_CHANNEL"] = channel;
        start.Environment["TEST_VERSION"] = version;
        start.Environment["WAGTERM_UPDATE_HOST_NAMESPACE"] = HostNamespace;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("update helper did not exit");
        }
        Assert.True(File.Exists(Result), $"Missing update result: {output.Result} {error.Result}");
        using var doc = JsonDocument.Parse(File.ReadAllText(Result));
        Assert.Equal(Log, doc.RootElement.GetProperty("logPath").GetString());
        return (process.ExitCode, doc.RootElement.Clone());
    }

    [Fact]
    public void InstallerFailureIsReportedAndOldAppRelaunched()
    {
        var run = Run(setupExit: 5);
        Assert.NotEqual(0, run.ExitCode);
        Assert.False(run.Result.GetProperty("success").GetBoolean());
        Assert.Contains("exit code 5", run.Result.GetProperty("error").GetString());
        Assert.DoesNotContain("setup finished successfully", File.ReadAllText(Log));
        Assert.True(File.Exists(Path.Combine(_dir, "relaunched")));
        Assert.Equal("old app", File.ReadAllText(Exe));
    }

    [Fact]
    public void SuccessDisablesProcessClosureAndRecordsInstallerLogAndDestination()
    {
        var run = Run();
        Assert.Equal(0, run.ExitCode);
        Assert.True(run.Result.GetProperty("success").GetBoolean());
        string[] args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(_dir, "setup-args.json")))!;
        Assert.Contains("/NOCLOSEAPPLICATIONS", args);
        Assert.Contains("/NORESTARTAPPLICATIONS", args);
        Assert.Contains("/RESTARTEXITCODE=3010", args);
        Assert.Contains($"/DIR=\"{Install}\"", args);
        Assert.Contains($"/LOG=\"{Log}.setup.log\"", args);
        Assert.Equal(File.ReadAllBytes(Pe), File.ReadAllBytes(Exe));
        Assert.True(File.Exists(Path.Combine(_dir, "relaunched")));
        Assert.Equal("", File.ReadAllText(Path.Combine(_dir, "relaunch-namespace")));
        using var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "stop-call.json")));
        Assert.Equal(Exe, call.RootElement.GetProperty("exe").GetString());
        Assert.Equal(HostNamespace, call.RootElement.GetProperty("ns").GetString());
    }

    [Theory]
    [InlineData("installed", false)]
    [InlineData("portable", false)]
    [InlineData("installed", true)]
    [InlineData("portable", true)]
    public void InstallsRealPeWithExpectedVersion(string channel, bool suffix)
    {
        Assert.NotEmpty(PeVersion);
        File.Copy(Pe, Path.Combine(_dir, "payload.exe"));
        string expected = PeVersion + (suffix ? "-test" : "");
        var run = Run(channel: channel, version: expected);
        Assert.Equal(0, run.ExitCode);
        Assert.True(run.Result.GetProperty("success").GetBoolean());
        Assert.Equal(expected, run.Result.GetProperty("version").GetString());
        Assert.Equal(File.ReadAllBytes(Pe), File.ReadAllBytes(Exe));
        Assert.Equal(channel == "installed", File.Exists(Path.Combine(_dir, "setup-args.json")));
        if (channel == "portable") Assert.Equal("old app", File.ReadAllText(Exe + ".old"));
    }

    [Fact]
    public void HostShutdownFailurePreventsInstallation()
    {
        var run = Run(stopExit: 1);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("terminal hosts could not stop", run.Result.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_dir, "setup-args.json")));
        Assert.Equal("old app", File.ReadAllText(Exe));
        Assert.True(File.Exists(Path.Combine(_dir, "relaunched")));
    }

    [Fact]
    public void AppStillRunningPreventsInstallationAndDuplicateRelaunch()
    {
        var run = Run(alive: true);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("app never exited", run.Result.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_dir, "setup-args.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "relaunched")));
        Assert.False(File.Exists(Path.Combine(_dir, "hosts-stopped")));
        Assert.False(File.Exists(Path.Combine(_dir, "stop-call.json")));
    }

    [Fact]
    public void VersionMismatchIsReported()
    {
        var run = Run(version: "999.0.0");
        Assert.NotEqual(0, run.ExitCode);
        Assert.False(run.Result.GetProperty("success").GetBoolean());
        Assert.Contains("expected WagTerm 999.0.0", run.Result.GetProperty("error").GetString());
        Assert.True(File.Exists(Path.Combine(_dir, "relaunched")));
    }

    [Fact]
    public void PortableSwapFailureRollsBack()
    {
        var run = Run(channel: "portable"); // Missing payload, after the original exe was moved aside.
        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("old app", File.ReadAllText(Exe));
        Assert.True(File.Exists(Path.Combine(_dir, "relaunched")));
    }

    [Fact]
    public void PortableVersionMismatchLeavesHostsAndExeUntouched()
    {
        string payload = Path.Combine(_dir, "payload.exe");
        File.Copy(Pe, payload);
        var run = Run(channel: "portable", version: "999.0.0");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("expected portable WagTerm 999.0.0", run.Result.GetProperty("error").GetString());
        Assert.Equal("old app", File.ReadAllText(Exe));
        Assert.Equal(File.ReadAllBytes(Pe), File.ReadAllBytes(payload));
        Assert.False(File.Exists(Exe + ".old"));
        Assert.False(File.Exists(Path.Combine(_dir, "stop-call.json")));
    }
}
