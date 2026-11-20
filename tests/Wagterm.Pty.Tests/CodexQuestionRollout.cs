using System.Text.Json;

namespace Wagterm.Pty.Tests;

/// <summary>Minimal Codex-style response items, surrounding metadata and structured async replies.</summary>
internal sealed class CodexQuestionRollout
{
    public string Path { get; }

    public CodexQuestionRollout(string directory)
    {
        Path = System.IO.Path.Combine(directory, "questions-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(Path, """{"type":"session_meta","payload":{"source":"cli"}}""" + "\n");
        UserMessage("<environment_context>Initial context</environment_context>");
    }

    public void Ask(string callId, int count = 1, string name = "request_user_input_async")
    {
        Append(new
        {
            type = "function_call", name, call_id = callId,
            arguments = JsonSerializer.Serialize(new
            {
                questions = Enumerable.Range(0, count).Select(i => new { title = $"Question {i}?" }).ToArray(),
            }),
        });
        Append(new { type = "function_call_output", call_id = callId, output = "{\"accepted\":true}" });
    }

    public void Answer(string callId, int index = 0)
        => Answers(callId, index);

    public void Answers(string callId, params int[] indexes)
    {
        string reply = JsonSerializer.Serialize(indexes.Select(index =>
            new { answer = "Yes", questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", callId, index }) }));
        UserMessage("<send_user_message_question_reply>\n" + reply + "\n</send_user_message_question_reply>");
    }

    public void UserMessage(string text)
        => Append(new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } });

    public void Abort(string shape)
    {
        if (shape == "event")
            File.AppendAllText(Path, JsonSerializer.Serialize(new
            {
                type = "event_msg", payload = new { type = "turn_aborted", reason = "interrupted" },
            }) + "\n");
        else if (shape == "developer")
            Append(new { type = "message", role = "developer", content = new[]
            {
                new { type = "input_text", text = "<turn_aborted>Cancelled by the user</turn_aborted>" },
            } });
        else if (shape == "user") UserMessage("<turn_aborted>Cancelled by the user</turn_aborted>");
        else throw new ArgumentException("Unknown cancellation shape", nameof(shape));
    }

    public void Noise(int bytes)
    {
        // Smoke workload for fast rejection of large non-candidate records. This does not model
        // a large population of parsed role:user records. Real-rollout timings are measured separately.
        string padding = new('x', 16 * 1024);
        using var writer = File.AppendText(Path);
        string[] records =
        {
            JsonSerializer.Serialize(new { type = "turn_context", payload = new { instructions = "request_user_input_async " + padding } }),
            JsonSerializer.Serialize(new { type = "compacted", payload = new { replacement_history = new[] { new { role = "user", content = padding } } } }),
            JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "function_call_output", output = "request_user_input_async " + padding } }),
        };
        for (int size = 0, i = 0; size < bytes; i++)
        {
            string record = records[i % records.Length];
            writer.WriteLine(record);
            size += record.Length + Environment.NewLine.Length;
        }
    }

    private void Append(object payload)
        => File.AppendAllText(Path, JsonSerializer.Serialize(new { type = "response_item", payload }) + "\n");
}
