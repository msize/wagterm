using Wagterm.Pty;

namespace Wagterm.Pty.Tests;

public class BrandingTests
{
    [Fact]
    public void UpdatesUseForkRepositoryAndEnvironment()
    {
        Assert.Equal("https://api.github.com/repos/msize/wagterm/releases/latest", AppUpdate.DefaultApiUrl);
        Assert.Equal("WAGTERM_UPDATE_API", AppUpdate.ApiEnvVar);
    }

    [Fact]
    public void HookFilesUseIndependentProductDirectory()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wagterm");
        Assert.Equal(Path.Combine(root, "wagterm-agent-status.ps1"), AgentHooks.WrapperPath);
        Assert.Equal(Path.Combine(root, "wagterm-agent-bind.ps1"), AgentHooks.AgentBindPath);
        Assert.Equal(Path.Combine(root, "wagterm-codex-hook.ps1"), AgentHooks.CodexHookPath);
        Assert.Equal(Path.Combine(root, "wagterm-codex-notify.ps1"), AgentHooks.CodexNotifyPath);
    }

    [Fact]
    public void AgentSkillUsesForkCliAndEnvironment()
    {
        Assert.Contains("name: wagterm", AgentSkill.SkillMarkdown);
        Assert.Contains("# WagTerm", AgentSkill.SkillMarkdown);
        Assert.Contains("WAGTERM_ENABLED=1", AgentSkill.SkillMarkdown);
        Assert.Contains("wagtermctl", AgentSkill.SkillMarkdown);
        Assert.Contains("WAGTERM_SESSION_ID", AgentHooks.WrapperScript);
        Assert.Contains("WAGTERM_PIPE", AgentHooks.WrapperScript);
    }

    [Fact]
    public void ProtobufDescriptorsUseForkPackagesAndNamespaces()
    {
        Assert.Equal("wagterm.ptyhost.Request", Proto.Request.Descriptor.FullName);
        Assert.Equal("Wagterm.Pty.Proto", Proto.Request.Descriptor.File.GetOptions().CsharpNamespace);
        Assert.Equal("wagterm.persist.PBuffer", Core.Persist.PBuffer.Descriptor.FullName);
        Assert.Equal("Wagterm.Core.Persist", Core.Persist.PBuffer.Descriptor.File.GetOptions().CsharpNamespace);
        Assert.Equal("wagterm.ptyhost.Hello", Proto.Request.Descriptor.FindFieldByName("hello").MessageType.FullName);
        Assert.Equal("wagterm.persist.PRow", Core.Persist.PBuffer.Descriptor.FindFieldByName("rows").MessageType.FullName);
    }

    [Theory]
    [InlineData("AGWINTERM", true)]
    [InlineData("agwinterm_session_id", true)]
    [InlineData("WAGTERM_SESSION_ID", false)]
    [InlineData("WAG_CWD", false)]
    [InlineData("PATH", false)]
    public void IdentifiesOnlyUpstreamEnvironment(string name, bool expected)
        => Assert.Equal(expected, ProductEnvironment.IsUpstreamVariable(name));

    [Fact]
    public void RemovesInheritedUpstreamIdentityWithoutTouchingForkOrUserVariables()
    {
        var env = new Dictionary<string, string>
        {
            ["AGWINTERM"] = "1",
            ["agwinterm_session_id"] = "upstream-pane",
            ["WAGTERM_SESSION_ID"] = "fork-pane",
            ["WAG_CWD"] = "fork-directory",
            ["PATH"] = "user-path",
        };
        ProductEnvironment.RemoveUpstreamVariables(env);
        Assert.Equal(3, env.Count);
        Assert.Equal("fork-pane", env["WAGTERM_SESSION_ID"]);
        Assert.Equal("fork-directory", env["WAG_CWD"]);
        Assert.Equal("user-path", env["PATH"]);
    }

    [Fact]
    public void InstallingForkHooksPreservesOtherProductHooks()
    {
        string other = @"C:\profile\other-terminal\agent-status.ps1";
        string fork = @"C:\profile\wagterm\wagterm-agent-status.ps1";
        string existing = AgentHooks.MergeClaudeSettings(null, other)!;
        string merged = AgentHooks.MergeClaudeSettings(existing, fork)!;
        using var json = System.Text.Json.JsonDocument.Parse(merged);
        foreach (var entry in json.RootElement.GetProperty("hooks").EnumerateObject())
        {
            string[] commands = entry.Value.EnumerateArray()
                .SelectMany(group => group.GetProperty("hooks").EnumerateArray())
                .Select(hook => hook.GetProperty("command").GetString()!).ToArray();
            Assert.Contains(commands, command => command.Contains(other));
            Assert.Contains(commands, command => command.Contains(fork));
        }
        Assert.Equal(merged, AgentHooks.MergeClaudeSettings(merged, fork));
    }

    [Theory]
    [InlineData(UpdateChannel.Installed, "otherterm-setup-1.0.0.exe", "wagterm-setup-1.0.0.exe")]
    [InlineData(UpdateChannel.Portable, "otherterm-portable-1.0.0-win-x64.exe", "wagterm-portable-1.0.0-win-x64.exe")]
    public void UpdaterNeverSelectsOtherProductArtifacts(UpdateChannel channel, string other, string fork)
    {
        var otherAsset = new ReleaseAsset(other, "unused", null);
        Assert.Null(AppUpdate.PickAsset(new ReleaseInfo("1.0.0", new[] { otherAsset }), channel));
        var forkAsset = new ReleaseAsset(fork, "unused", null);
        Assert.Same(forkAsset, AppUpdate.PickAsset(new ReleaseInfo("1.0.0", new[] { otherAsset, forkAsset }), channel));
    }
}
