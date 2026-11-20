using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wagterm.Pty;

/// <summary>Opt-in text notifications, independent of status/resume installation and Codex's notify program.</summary>
public static partial class AgentNotifications
{
    /// <summary>Install the shared script and merge notification hooks into the user's agent configs.</summary>
    public static string Install()
    {
        try
        {
            return Install(Path.GetDirectoryName(AgentHooks.WrapperPath)!,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AgentHooks.ClaudeSettingsPath, AgentHooks.CodexHooksPath, AgentHooks.DevinConfigPath);
        }
        catch (Exception ex) { return "Notifications: failed to install: " + ex.Message; }
    }

    internal static string Install(string directory, string home, string claude, string codex, string devin)
    {
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "wagterm-agent-notify.ps1");
        WriteAtomically(script, Script);
        var lines = new List<string>();
        foreach (var (agent, name, path) in new[]
        {
            ("claude", "Claude", claude), ("codex", "Codex", codex), ("devin", "Devin", devin),
        })
        {
            try
            {
                string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
                string? merged = Merge(existing, script, agent, home);
                if (merged is null)
                {
                    lines.Add($"{name}: refused (invalid JSON or hook structure), left untouched -> {path}");
                    continue;
                }
                if (existing != merged)
                {
                    if (existing is not null)
                    {
                        string backup = path + ".wagterm.bak";
                        for (int i = 2; File.Exists(backup); i++) backup = path + $".wagterm-{i}.bak";
                        File.Copy(path, backup);
                        lines.Add($"{name}: original config backup -> {backup}");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Preserve existing links and access rules, like the status-hook installer.
                    if (existing is not null) File.WriteAllText(path, merged);
                    else WriteAtomically(path, merged);
                }
                lines.Add($"{name}: notifications -> {path}");
            }
            catch (Exception ex) { lines.Add($"{name}: failed to install notifications: {ex.Message}"); }
        }
        lines.Add("Restart Claude, Codex and Devin to load the hooks. In Codex, open /hooks and enable/trust the new hooks.");
        lines.Add("Desktop delivery still follows desktop-notifications and Windows notification settings.");
        return string.Join("\n", lines);
    }

    internal static string? Merge(string? existing, string script, string agent, string home)
    {
        if (agent is not ("claude" or "codex" or "devin")) throw new ArgumentException("Unknown agent", nameof(agent));
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existing) ? new JsonObject() :
                JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = agent == "devin" ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
                    AllowTrailingCommas = agent == "devin",
                }) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException) { return null; }

        if (root.ContainsKey("hooks") && root["hooks"] is not JsonObject) return null;
        bool changed = !root.ContainsKey("hooks");
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        if (changed) root["hooks"] = hooks;
        if (hooks.Any(e => e.Value is not JsonArray groups ||
            groups.Any(g => g is not JsonObject || g["hooks"] is not JsonArray))) return null;

        // A matching filename alone does not establish ownership of a user's command.
        string command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" {agent}";
        var events = new (string Event, string? Matcher)[]
        {
            ("Stop", null),
            (agent == "claude" ? "Notification" : "PermissionRequest", agent == "claude" ? "permission_prompt" : null),
            ("PreToolUse", agent switch
            {
                "claude" => "^AskUserQuestion$",
                "devin" => "^ask_user_question$",
                _ => @"^(?:functions\.)?(?:request_user_input(?:_async)?|ask_user_question)$",
            }),
            ("UserPromptSubmit", null),
            ("PostToolUse", null),
        };
        foreach (var (evt, matcher) in events)
        {
            var groups = hooks[evt] as JsonArray ?? new JsonArray();
            if (!hooks.ContainsKey(evt))
            {
                hooks[evt] = groups;
                changed = true;
            }
            bool installed = false;
            foreach (var group in groups.ToArray())
            {
                var handlers = group!["hooks"]!.AsArray();
                bool removed = false;
                foreach (var handler in handlers.ToArray())
                {
                    if (handler is not JsonObject h || h["command"] is not JsonValue value ||
                        !value.TryGetValue<string>(out var previous)) continue;
                    if (previous == command || IsLegacyHook(previous, agent, home, evt))
                    {
                        bool sameMatcher = JsonNode.DeepEquals(group["matcher"], JsonValue.Create(matcher));
                        if (previous == command && !installed && (sameMatcher || handlers.Count == 1))
                        {
                            // Codex indexes trust/enabled state by group and handler position. Upgrade
                            // a group owned solely by this hook in place without shifting its neighbours.
                            if (!sameMatcher)
                            {
                                if (matcher is null) group.AsObject().Remove("matcher");
                                else group["matcher"] = matcher;
                                changed = true;
                            }
                            installed = true;
                            continue;
                        }
                        handlers.Remove(handler);
                        removed = true;
                        changed = true;
                    }
                }
                if (removed && handlers.Count == 0) groups.Remove(group);
            }
            if (installed) continue;
            var entry = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command", ["command"] = command, ["timeout"] = 10,
                }),
            };
            if (matcher is not null) entry["matcher"] = matcher;
            groups.Add(entry);
            changed = true;
        }
        return changed ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : existing;
    }

    private static bool IsLegacyHook(string command, string agent, string home, string evt)
    {
        if (agent == "codex") return false;
        var match = Regex.Match(command,
            "^bash\\s+(?:\"(?<path>[^\"]+)\"|'(?<path>[^']+)'|(?<path>\\S+))\\s+(?<mode>stop|permission|question)$");
        if (!match.Success) return false;
        string expectedEvent = match.Groups["mode"].Value switch
        {
            "stop" => "Stop", "question" => "PreToolUse",
            _ => agent == "claude" ? "Notification" : "PermissionRequest",
        };
        if (evt != expectedEvent) return false;
        string path = match.Groups["path"].Value.Replace('\\', '/');
        string user = home.Replace('\\', '/').TrimEnd('/');
        foreach (string prefix in new[] { "$HOME", "${HOME}", "~" })
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal)) path = user + path[prefix.Length..];
        if (path.Length > 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == '/')
            path = path[1] + ":" + path[2..];
        string relative = agent == "claude" ? "/.claude/hooks/wagterm-notify.sh" : "/.config/devin/hooks/wagterm-notify.sh";
        return path.Equals(user + relative, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteAtomically(string path, string text)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
