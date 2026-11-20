using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wagterm.Pty;
using Xunit.Abstractions;

namespace Wagterm.Pty.Tests;

public class AgentNotificationScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wagterm-notify-script-" + Guid.NewGuid().ToString("N"));
    private readonly string _script;
    private readonly ITestOutputHelper _output;

    public AgentNotificationScriptTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        _script = Path.Combine(_dir, "notify.ps1");
        File.WriteAllText(_script, AgentNotifications.Script);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private List<JsonElement> Run(string agent, string input, Dictionary<string, string?>? env = null)
    {
        string pipeName = "wagterm-test-notify-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream NewListener() => new(pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var listener = NewListener();
        using var stop = new CancellationTokenSource();
        var received = new List<JsonElement>();
        Task read = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await listener.WaitForConnectionAsync(stop.Token);
                    using var server = listener;
                    listener = NewListener();
                    var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                    string? line = await reader.ReadLineAsync(stop.Token);
                    using var doc = JsonDocument.Parse(line!);
                    received.Add(doc.RootElement.Clone());
                    var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    try { await writer.WriteLineAsync("{\"ok\":true}"); }
                    catch (IOException) { }
                }
            }
            catch (OperationCanceledException) { }
            finally { listener.Dispose(); }
        });
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", _script, agent })
            start.ArgumentList.Add(arg);
        foreach (string key in new[] { "DEVIN_PROJECT_DIR", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED", "WAGTERM_WINDOW_ID", "WAGTERM_CODEX_APPROVAL_ALERTS" })
            start.Environment.Remove(key);
        start.Environment["WAGTERM_SESSION_ID"] = "pane-test";
        start.Environment["WAGTERM_PIPE"] = @"\\.\pipe\" + pipeName;
        foreach (var (key, value) in env ?? new()) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(30_000), "notification hook did not exit");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", process.StandardOutput.ReadToEnd());
        Assert.Equal("", process.StandardError.ReadToEnd());
        stop.Cancel();
        Assert.True(read.Wait(5000), "pipe reader did not stop");
        foreach (var request in received) Assert.Equal("pane-test", request.GetProperty("target").GetString());
        return received;
    }

    private List<JsonElement> Event(string agent, string name, object? fields = null, Dictionary<string, string?>? env = null)
    {
        var data = new Dictionary<string, object?> { ["hook_event_name"] = name, ["session_id"] = "test" };
        if (fields is not null)
            foreach (var field in JsonSerializer.SerializeToElement(fields).EnumerateObject()) data[field.Name] = field.Value;
        return Run(agent, JsonSerializer.Serialize(data), env);
    }

    private static JsonElement Notification(List<JsonElement> requests, string status)
    {
        Assert.Equal(2, requests.Count);
        Assert.Equal("session.status", requests[0].GetProperty("cmd").GetString());
        Assert.Equal(status, requests[0].GetProperty("args").GetProperty("status").GetString());
        Assert.Equal("notify", requests[1].GetProperty("cmd").GetString());
        return requests[1].GetProperty("args");
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void StopIncludesUnicodeTextAndProject(string agent)
    {
        var note = Notification(Event(agent, "Stop", new
        {
            cwd = @"C:\src\project", last_assistant_message = "  \u0413\u043e\u0442\u043e\u0432\u043e.\r\nTests pass.  ",
        }), "completed");
        Assert.Equal("\u0413\u043e\u0442\u043e\u0432\u043e. Tests pass.", note.GetProperty("body").GetString());
        Assert.EndsWith(" finished (project)", note.GetProperty("title").GetString());
        Assert.Equal("ok", note.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("claude", "Stop")]
    [InlineData("codex", "Stop")]
    [InlineData("devin", "Stop")]
    [InlineData("claude", "PostToolUse")]
    [InlineData("codex", "PostToolUse")]
    [InlineData("devin", "PostToolUse")]
    public void RequestsTargetTheOwningWindow(string agent, string name)
    {
        var requests = Event(agent, name, env: new() { ["WAGTERM_WINDOW_ID"] = "background-window" });
        if (name == "Stop") Notification(requests, "completed");
        else Assert.Single(requests);
        Assert.All(requests, request => Assert.Equal("background-window", request.GetProperty("window").GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingWindowIdentityOmitsWindowSelector(string? window)
    {
        var requests = Event("claude", "Stop", env: new() { ["WAGTERM_WINDOW_ID"] = window });
        Notification(requests, "completed");
        Assert.All(requests, request => Assert.False(request.TryGetProperty("window", out _)));
    }

    [Fact]
    public void DevinStopUsesDocumentedPayloadAndProjectDirectory()
    {
        var note = Notification(Event("devin", "Stop", new { prompt_id = "turn-test", stop_hook_active = false },
            env: new() { ["DEVIN_PROJECT_DIR"] = @"C:\src\project" }), "completed");
        Assert.Equal("Turn completed", note.GetProperty("body").GetString());
        Assert.Equal("Devin finished (project)", note.GetProperty("title").GetString());
        Assert.Equal("ok", note.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("claude", "AskUserQuestion")]
    [InlineData("devin", "ask_user_question")]
    [InlineData("codex", "request_user_input")]
    public void QuestionNotifiesAndBlocks(string agent, string tool)
    {
        var note = Notification(Event(agent, "PreToolUse", new
        {
            tool_name = tool, tool_input = new { questions = new[] { new { question = "Choose a library?" } } },
        }), "blocked");
        Assert.Equal("Choose a library?", note.GetProperty("body").GetString());
        Assert.Equal("attention", note.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("request_user_input_async")]
    [InlineData("functions.request_user_input_async")]
    public void AsyncQuestionIncludesItsTitleInTuiApprovalMode(string tool)
    {
        var note = Notification(Event("codex", "PreToolUse", new
        {
            tool_name = tool, tool_input = new { questions = new[] { new { title = "Choose a branch?" } } },
        }, env: new() { ["WAGTERM_CODEX_APPROVAL_ALERTS"] = "tui" }), "blocked");
        Assert.Equal("Choose a branch?", note.GetProperty("body").GetString());
        Assert.Equal("attention", note.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("PostToolUse")]
    [InlineData("Stop")]
    public void AsyncQuestionRemainsBlockedUntilItsAnswer(string name)
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("branch-choice");
        void Check(string status)
        {
            var requests = Event("codex", name, new { transcript_path = rollout.Path, last_assistant_message = "Done." });
            if (name == "Stop")
            {
                var note = Notification(requests, status);
                Assert.Equal("Done.", note.GetProperty("body").GetString());
                if (status == "blocked")
                {
                    Assert.EndsWith("needs your answer", note.GetProperty("title").GetString());
                    Assert.Equal("attention", note.GetProperty("category").GetString());
                }
            }
            else Assert.Equal(status, Assert.Single(requests).GetProperty("args").GetProperty("status").GetString());
        }
        Check("blocked");
        rollout.Answer("branch-choice");
        Check(name == "Stop" ? "completed" : "active");
    }

    [Fact]
    public void PartialAndUnrelatedAnswersKeepOtherQuestionsPending()
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("first", 2);
        rollout.Ask("second");
        void Check(string status)
            => Assert.Equal(status, Assert.Single(Event("codex", "PostToolUse", new { transcript_path = rollout.Path }))
                .GetProperty("args").GetProperty("status").GetString());
        rollout.Answer("first", 0);
        Check("blocked");
        rollout.Answer("unrelated");
        Check("blocked");
        rollout.Answer("first", 1);
        Check("blocked");
        rollout.Answer("second");
        Check("active");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryIndexOfASingleCallMustBeAnswered(bool combinedReply)
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("only-call", 2);
        if (combinedReply) rollout.Answers("only-call", 0, 1);
        else
        {
            rollout.Answer("only-call", 0);
            var pending = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
            Assert.Equal("blocked", Assert.Single(pending).GetProperty("args").GetProperty("status").GetString());
            rollout.Answer("only-call", 1);
        }
        var answered = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("active", Assert.Single(answered).GetProperty("args").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("<environment_context>New date</environment_context>")]
    [InlineData("<user_action>Client metadata</user_action>")]
    [InlineData("<recommended_plugins>Client metadata</recommended_plugins>")]
    [InlineData("Chat from Claude: you have inbox mail from reviewer: changes requested")]
    [InlineData("Chat from reviewer: you have inbox mail from reviewer: changes requested")]
    [InlineData("Chat from Kimi: you have inbox mail from reviewer: changes requested")]
    [InlineData("Chat from WagFlow: you have inbox mail from author: changes requested")]
    [InlineData("# AGENTS.md instructions for C:/src/project\nProject instructions")]
    public void InjectedUserItemsDoNotClearTheRemainingQuestion(string text)
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("call", 2);
        rollout.Answer("call", 0);
        rollout.UserMessage(text);
        var pending = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("blocked", Assert.Single(pending).GetProperty("args").GetProperty("status").GetString());
        rollout.Answer("call", 1);
        var answered = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("active", Assert.Single(answered).GetProperty("args").GetProperty("status").GetString());
    }

    [Fact]
    public void TruncatedReplyEnvelopeCannotCauseRegexBacktracking()
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("call");
        rollout.UserMessage("<send_user_message_question_reply>" + new string(' ', 8000));
        var requests = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("active", Assert.Single(requests).GetProperty("args").GetProperty("status").GetString());
    }

    [Fact]
    public void LargeRolloutStopFinishesWithinTheNotificationHookTimeout()
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Noise(31 * 1024 * 1024);
        rollout.Ask("pending");
        var watch = Stopwatch.StartNew();
        Notification(Event("codex", "Stop", new { transcript_path = rollout.Path, last_assistant_message = "Done." }), "blocked");
        watch.Stop();
        _output.WriteLine($"Whole Stop hook, {new FileInfo(rollout.Path).Length} bytes: {watch.Elapsed.TotalSeconds:F3} seconds.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Stop hook exceeded its configured timeout: {watch.Elapsed}");
    }

    [Fact]
    public void PreToolUseToleratesALockedTranscript()
    {
        string path = Path.Combine(_dir, "unreadable.jsonl");
        File.WriteAllText(path, """{"type":"session_meta","payload":{"source":"cli"}}""" + "\n");
        using var writer = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var note = Notification(Event("codex", "PreToolUse", new
        {
            transcript_path = path, tool_name = "request_user_input_async",
            tool_input = new { questions = new[] { new { title = "Choose?" } } },
        }), "blocked");
        Assert.Equal("Choose?", note.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingQuestionSurvivesAnOpenWriterAndAnIncompleteLastRecord(bool namespaced)
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("call", name: namespaced ? "functions.request_user_input_async" : "request_user_input_async");
        File.AppendAllText(rollout.Path, """{"type":"response_item","payload":{"role":"user","content":""");
        using var writer = File.Open(rollout.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var requests = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("blocked", Assert.Single(requests).GetProperty("args").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Skip that question and continue.")]
    [InlineData("<turn_aborted>Cancelled by the user</turn_aborted>")]
    public void NewUserInstructionOrCancellationSupersedesAnOldAsyncQuestion(string text)
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("old");
        rollout.UserMessage(text);
        var requests = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("active", Assert.Single(requests).GetProperty("args").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("event")]
    [InlineData("developer")]
    [InlineData("user")]
    public void CancellationClearsQuestionsBeforeATurnStartedByPeerMail(string shape)
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("cancelled", 2);
        rollout.Answer("cancelled", 0);
        rollout.Abort(shape);
        rollout.UserMessage("Chat from reviewer: you have inbox mail from reviewer: please continue");
        var afterAbort = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("active", Assert.Single(afterAbort).GetProperty("args").GetProperty("status").GetString());
        rollout.Ask("new");
        var afterNewQuestion = Event("codex", "PostToolUse", new { transcript_path = rollout.Path });
        Assert.Equal("blocked", Assert.Single(afterNewQuestion).GetProperty("args").GetProperty("status").GetString());
    }

    [Fact]
    public void NotificationPromptSubmitResumesWhileAnEarlierQuestionIsPending()
    {
        var rollout = new CodexQuestionRollout(_dir);
        rollout.Ask("old");
        var requests = Event("codex", "UserPromptSubmit", new { transcript_path = rollout.Path });
        Assert.Equal("active", Assert.Single(requests).GetProperty("args").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("codex", "Bash")]
    [InlineData("devin", "exec")]
    public void PermissionIncludesCommand(string agent, string tool)
    {
        var note = Notification(Event(agent, "PermissionRequest", new
        {
            tool_name = tool, tool_input = new { command = "git status" },
        }), "blocked");
        Assert.Equal($"{tool}: git status", note.GetProperty("body").GetString());
    }

    [Fact]
    public void CodexTuiApprovalModeSuppressesThePrematurePermissionHook()
        => Assert.Empty(Event("codex", "PermissionRequest", new
        {
            tool_name = "Bash", tool_input = new { command = "git status" },
        }, env: new() { ["WAGTERM_CODEX_APPROVAL_ALERTS"] = "tui" }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hooks")]
    [InlineData("unknown")]
    [InlineData("TUI")]
    public void CodexPermissionAlertsRemainEnabledWithoutAnExactOptIn(string? mode)
        => Notification(Event("codex", "PermissionRequest",
            env: new() { ["WAGTERM_CODEX_APPROVAL_ALERTS"] = mode }), "blocked");

    [Theory]
    [InlineData("claude", "Notification")]
    [InlineData("devin", "PermissionRequest")]
    public void OtherAgentsIgnoreCodexTuiApprovalMode(string agent, string name)
        => Notification(Event(agent, name, new
        {
            notification_type = "permission_prompt", message = "Approve command?",
        }, env: new() { ["WAGTERM_CODEX_APPROVAL_ALERTS"] = "tui" }), "blocked");

    [Theory]
    [InlineData("Stop", "completed")]
    [InlineData("PreToolUse", "blocked")]
    [InlineData("PostToolUse", "active")]
    [InlineData("UserPromptSubmit", "active")]
    public void CodexTuiApprovalModeKeepsOtherHookEvents(string name, string status)
    {
        var requests = Event("codex", name, new
        {
            tool_name = "request_user_input", last_assistant_message = "Done.",
        }, env: new() { ["WAGTERM_CODEX_APPROVAL_ALERTS"] = "tui" });
        if (name is "PostToolUse" or "UserPromptSubmit")
            Assert.Equal(status, Assert.Single(requests).GetProperty("args").GetProperty("status").GetString());
        else Notification(requests, status);
    }

    [Fact]
    public void ClaudePermissionIncludesMessage()
    {
        var note = Notification(Event("claude", "Notification", new
        {
            notification_type = "permission_prompt", message = "Approve command?",
        }), "blocked");
        Assert.Equal("Approve command?", note.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("codex", "blocked", "needs your answer", "attention")]
    [InlineData("claude", "completed", "finished", "ok")]
    public void StopOnQuestionUsesAgentSpecificState(string agent, string status, string title, string category)
    {
        var note = Notification(Event(agent, "Stop", new { last_assistant_message = "Shall I push?  " }), status);
        Assert.EndsWith(title, note.GetProperty("title").GetString());
        Assert.Equal(category, note.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("UserPromptSubmit")]
    [InlineData("PostToolUse")]
    public void ResumingWorkRestoresActive(string name)
    {
        var request = Assert.Single(Event("devin", name));
        Assert.Equal("active", request.GetProperty("args").GetProperty("status").GetString());
    }

    [Fact]
    public void MissingTextHasFallback()
        => Assert.Equal("Turn completed", Notification(Event("devin", "Stop"), "completed").GetProperty("body").GetString());

    [Fact]
    public void MissingQuestionHasFallback()
        => Assert.Equal("Waiting for your answer", Notification(Event("devin", "PreToolUse",
            new { tool_name = "ask_user_question" }), "blocked").GetProperty("body").GetString());

    [Fact]
    public void LongTextIsBoundedWithoutSplittingSurrogates()
    {
        string text = new string('a', 198) + "\ud83d\ude00" + new string('b', 20);
        var note = Notification(Event("claude", "Stop", new { last_assistant_message = text }), "completed");
        Assert.Equal(new string('a', 198) + "\u2026", note.GetProperty("body").GetString());
    }

    [Fact]
    public void ClaudeHookImportedByDevinIsSilent()
        => Assert.Empty(Event("claude", "Stop", env: new() { ["DEVIN_PROJECT_DIR"] = @"C:\src\project" }));

    [Theory]
    [InlineData("sdk-cli", "0")]
    [InlineData("cli", "0")]
    [InlineData("sdk-cli", null)]
    public void HeadlessClaudeIsSilent(string entrypoint, string? attended)
        => Assert.Empty(Event("claude", "Stop", env: new()
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = entrypoint, ["CLAUDE_CODE_SESSION_ATTENDED"] = attended,
        }));

    [Fact]
    public void InteractiveClaudeWithoutAttendedFlagNotifies()
        => Notification(Event("claude", "Stop", env: new() { ["CLAUDE_CODE_ENTRYPOINT"] = "cli" }), "completed");

    [Fact]
    public void NestedCodexExecIsSilent()
    {
        string rollout = Path.Combine(_dir, "rollout.jsonl");
        File.WriteAllText(rollout, """{"payload":{"source":"exec"}}""");
        Assert.Empty(Event("codex", "Stop", new { transcript_path = rollout }));
    }

    [Theory]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"source\":\"cli\"}}")]
    [InlineData("{broken")]
    public void CodexInteractiveOrUnreadableTranscriptStillNotifies(string metadata)
    {
        string rollout = Path.Combine(_dir, "rollout.jsonl");
        File.WriteAllText(rollout, metadata);
        Notification(Event("codex", "Stop", new { transcript_path = rollout, last_assistant_message = "Done" }), "completed");
    }

    [Fact]
    public void MalformedInputIsSilent() => Assert.Empty(Run("devin", "{broken"));

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("devin")]
    public void OutsideWagtermPushesNothing(string agent)
        => Assert.Empty(Event(agent, "Stop", new { last_assistant_message = "Done" },
            env: new() { ["WAGTERM_SESSION_ID"] = null }));
}
