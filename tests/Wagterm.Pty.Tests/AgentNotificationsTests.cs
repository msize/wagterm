using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Wagterm.Pty;

namespace Wagterm.Pty.Tests;

public class AgentNotificationsTests : IDisposable
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr securityAttributes);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wagterm-notifications-" + Guid.NewGuid().ToString("N"));
    private const string Script = @"C:\Users\x\AppData\Local\wagterm\wagterm-agent-notify.ps1";
    private const string Home = @"C:\Users\x";

    public AgentNotificationsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("claude", "Notification", "permission_prompt", "^AskUserQuestion$")]
    [InlineData("devin", "PermissionRequest", null, "^ask_user_question$")]
    [InlineData("codex", "PermissionRequest", null, @"^(?:functions\.)?(?:request_user_input(?:_async)?|ask_user_question)$")]
    public void MergeAddsEventsAndPreservesExistingSettings(string agent, string permission, string? matcher, string question)
    {
        const string original = """{"other":42,"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo mine"}]}]}}""";
        string once = AgentNotifications.Merge(original, Script, agent, Home)!;
        Assert.Equal(once, AgentNotifications.Merge(once, Script, agent, Home));
        var root = JsonNode.Parse(once)!;
        Assert.Equal(42, root["other"]!.GetValue<int>());
        var hooks = root["hooks"]!;
        Assert.Equal("echo mine", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(2, hooks["Stop"]!.AsArray().Count);
        Assert.Equal(matcher, hooks[permission]![0]!["matcher"]?.GetValue<string>());
        Assert.Equal(question, hooks["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        foreach (string evt in new[] { "Stop", permission, "PreToolUse", "UserPromptSubmit", "PostToolUse" })
            Assert.Contains(hooks[evt]!.AsArray(), e => e!["hooks"]!.AsArray().Any(
                h => h!["command"]!.GetValue<string>().EndsWith($"\"{Script}\" {agent}", StringComparison.Ordinal)));
        Assert.Null(hooks["SessionStart"]);
        Assert.Null(root["notify"]);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("""{"hooks":42}""")]
    [InlineData("""{"hooks":{"Stop":{}}}""")]
    [InlineData("""{"hooks":{"Stop":[{"hooks":"mine"}]}}""")]
    public void MalformedConfigIsRefused(string original)
        => Assert.Null(AgentNotifications.Merge(original, Script, "devin", Home));

    [Fact]
    public void DevinAcceptsJsonc()
        => Assert.NotNull(AgentNotifications.Merge("{ // comment\n \"value\": 1, }", Script, "devin", Home));

    [Fact]
    public void ReinstallPreservesHookOrderAndFormattingAfterStatusInstall()
    {
        const string statusScript = @"C:\Users\x\AppData\Local\wagterm\wagterm-agent-status.ps1";
        string status = AgentHooks.MergeClaudeSettings(null, statusScript)!;
        string notifications = AgentNotifications.Merge(status, Script, "claude", Home)!;
        string both = AgentHooks.MergeClaudeSettings(notifications, statusScript, @"C:\bind.ps1")!;
        var document = JsonNode.Parse(both)!;
        document["hooks"]!["Stop"]!.AsArray().Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "echo after" }),
        });
        string formatted = document.ToJsonString();
        Assert.Equal(formatted, AgentNotifications.Merge(formatted, Script, "claude", Home));
        var stop = document["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(3, stop.Count);
        Assert.Contains(statusScript, stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Contains(Script, stop[1]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void ReinstallUpgradesTheCodexQuestionMatcherWithoutDuplicatingHandlers()
    {
        string previous = AgentNotifications.Merge(null, Script, "codex", Home)!;
        var root = JsonNode.Parse(previous)!;
        root["hooks"]!["PreToolUse"]![0]!["matcher"] = "^(request_user_input|ask_user_question)$";
        var neighbour = new JsonObject
        {
            ["matcher"] = "exec_command",
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "echo user-owned" }),
        };
        root["hooks"]!["PreToolUse"]!.AsArray().Add(neighbour);
        string updated = AgentNotifications.Merge(root.ToJsonString(), Script, "codex", Home)!;
        var groups = JsonNode.Parse(updated)!["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.True(JsonNode.DeepEquals(neighbour, groups[1]));
        string matcher = groups[0]!["matcher"]!.GetValue<string>();
        foreach (string tool in new[] { "request_user_input", "request_user_input_async", "functions.request_user_input_async", "ask_user_question" })
            Assert.Matches(matcher, tool);
        Assert.DoesNotMatch(matcher, "exec_command");
        Assert.Equal(updated, AgentNotifications.Merge(updated, Script, "codex", Home));
    }

    [Theory]
    [InlineData("claude", "bash /c/Users/x/.claude/hooks/wagterm-notify.sh stop")]
    [InlineData("devin", "bash \"$HOME/.config/devin/hooks/wagterm-notify.sh\" stop")]
    public void KnownManualHooksAreMigratedWithoutDeletingTheirNeighbours(string agent, string command)
    {
        var original = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(
                        new JsonObject { ["type"] = "command", ["command"] = command },
                        new JsonObject { ["type"] = "command", ["command"] = "echo keep" }),
                }),
            },
        };
        var root = JsonNode.Parse(AgentNotifications.Merge(original.ToJsonString(), Script, agent, Home)!)!;
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal("echo keep", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(2, stop.Count);
        Assert.DoesNotContain(command, root.ToJsonString());
    }

    [Theory]
    [InlineData("claude", "Notification", "bash '~/.claude/hooks/wagterm-notify.sh' permission", true)]
    [InlineData("claude", "PreToolUse", "bash \"${HOME}/.claude/hooks/wagterm-notify.sh\" question", true)]
    [InlineData("devin", "PermissionRequest", "bash \"$HOME/.config/devin/hooks/wagterm-notify.sh\" permission", true)]
    [InlineData("devin", "PreToolUse", @"bash 'C:\Users\x\.config\devin\hooks\wagterm-notify.sh' question", true)]
    [InlineData("claude", "PreToolUse", "bash ~/.claude/hooks/wagterm-notify.sh stop", false)]
    [InlineData("codex", "Stop", "bash ~/.claude/hooks/wagterm-notify.sh stop", false)]
    public void LegacyModesRespectAgentPathAndEvent(string agent, string evt, string command, bool migrated)
    {
        string original = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                [evt] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
                }),
            },
        }.ToJsonString();
        string merged = AgentNotifications.Merge(original, Script, agent, Home)!;
        var groups = JsonNode.Parse(merged)!["hooks"]![evt]!.AsArray();
        bool remains = groups.Any(group => group!["hooks"]!.AsArray().Any(
            handler => handler!["command"]!.GetValue<string>() == command));
        Assert.Equal(!migrated, remains);
        Assert.Equal(merged, AgentNotifications.Merge(merged, Script, agent, Home));
    }

    [Fact]
    public void SimilarUserScriptIsNotOwned()
    {
        const string command = "bash /custom/wagterm-notify.sh stop";
        string original = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
                }),
            },
        }.ToJsonString();
        Assert.Contains(command, AgentNotifications.Merge(original, Script, "claude", Home)!);
    }

    [Fact]
    public void InstallBacksUpAllConfigsAndIsIdempotent()
    {
        string[] configs = { "claude.json", "codex.json", "devin.json" };
        foreach (string file in configs) File.WriteAllText(Path.Combine(_dir, file), """{"keep":true}""");
        string result = Install();
        Assert.Contains("Restart", result);
        Assert.Contains("/hooks", result);
        foreach (string file in configs)
            Assert.Equal("""{"keep":true}""", File.ReadAllText(Path.Combine(_dir, file + ".wagterm.bak")));
        string second = Install();
        Assert.DoesNotContain("backup ->", second);
        Assert.Equal(3, Directory.GetFiles(_dir, "*.bak").Length);
        Assert.Equal(AgentNotifications.Script, File.ReadAllText(Path.Combine(_dir, "wagterm-agent-notify.ps1")));
    }

    [Fact]
    public void InvalidAgentConfigDoesNotStopTheOtherAgents()
    {
        File.WriteAllText(Path.Combine(_dir, "claude.json"), "{broken");
        string result = Install();
        Assert.Contains("Claude: refused", result);
        Assert.Contains("Codex: notifications ->", result);
        Assert.Contains("Devin: notifications ->", result);
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(_dir, "claude.json")));
    }

    [Fact]
    public void ExistingBackupIsNeverOverwritten()
    {
        string path = Path.Combine(_dir, "devin.json");
        File.WriteAllText(path, "{ // original comment\n}");
        File.WriteAllText(path + ".wagterm.bak", "previous backup");
        Install();
        Assert.Equal("previous backup", File.ReadAllText(path + ".wagterm.bak"));
        Assert.Equal("{ // original comment\n}", File.ReadAllText(path + ".wagterm-2.bak"));
    }

    [Fact]
    public void InstallPreservesExistingConfigHardLinks()
    {
        string target = Path.Combine(_dir, "dotfiles.json");
        string config = Path.Combine(_dir, "claude.json");
        const string original = """{"keep":true}""";
        File.WriteAllText(target, original);
        Assert.True(CreateHardLink(config, target, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastPInvokeError()}");
        Install();
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(target))!["hooks"]);
        Assert.Equal(File.ReadAllText(target), File.ReadAllText(config));
        Assert.Equal(original, File.ReadAllText(config + ".wagterm.bak"));
    }

    private string Install() => AgentNotifications.Install(_dir, Home,
        Path.Combine(_dir, "claude.json"), Path.Combine(_dir, "codex.json"), Path.Combine(_dir, "devin.json"));
}
