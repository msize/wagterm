namespace Wagterm.Pty;

/// <summary>Reads Codex async questions and replies for status/notification hooks. Ordinary prompts and
/// turn cancellation supersede pending questions; injected context and peer mail do not.</summary>
internal static class CodexQuestionState
{
    internal const string Script =
        """
        function Test-WagtermPendingQuestion($eventData) {
          if (-not $eventData.transcript_path) { return $false }
          $reader = $null
          $pending = @{}
          try {
            # Read the owning rollout with sharing enabled while Codex appends to it. No persistent
            # marker can leak a pending question into another session or survive a transcript reset.
            $stream = [IO.File]::Open($eventData.transcript_path, [IO.FileMode]::Open,
              [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
            while ($null -ne ($line = $reader.ReadLine())) {
              # Check only a bounded record header with ordinal comparisons. Instructions, compacted
              # history and tool outputs may quote the tool name without being question records.
              $head = $line.Substring(0, [Math]::Min(512, $line.Length))
              $response = $head.IndexOf('"type":"response_item"', [StringComparison]::Ordinal) -ge 0 -or
                          $head.IndexOf('"type": "response_item"', [StringComparison]::Ordinal) -ge 0
              $aborted = $head.IndexOf('"type":"event_msg"', [StringComparison]::Ordinal) -ge 0 -or
                         $head.IndexOf('"type": "event_msg"', [StringComparison]::Ordinal) -ge 0
              $aborted = $aborted -and ($head.IndexOf('"type":"turn_aborted"', [StringComparison]::Ordinal) -ge 0 -or
                                       $head.IndexOf('"type": "turn_aborted"', [StringComparison]::Ordinal) -ge 0)
              if (-not $response -and -not $aborted) { continue }
              $call = $head.IndexOf('"type":"function_call"', [StringComparison]::Ordinal) -ge 0 -or
                      $head.IndexOf('"type": "function_call"', [StringComparison]::Ordinal) -ge 0
              $user = $head.IndexOf('"role":"user"', [StringComparison]::Ordinal) -ge 0 -or
                      $head.IndexOf('"role": "user"', [StringComparison]::Ordinal) -ge 0
              $developerAbort = $head.IndexOf('turn_aborted', [StringComparison]::Ordinal) -ge 0 -and
                  ($head.IndexOf('"role":"developer"', [StringComparison]::Ordinal) -ge 0 -or
                   $head.IndexOf('"role": "developer"', [StringComparison]::Ordinal) -ge 0)
              if (-not $user -and -not $aborted -and -not $developerAbort -and
                  (-not $call -or $head.IndexOf('request_user_input_async', [StringComparison]::Ordinal) -lt 0)) { continue }
              try { $record = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
              if ($record.type -eq 'event_msg' -and $record.payload.type -eq 'turn_aborted') {
                $pending.Clear()
                continue
              }
              if ($record.type -ne 'response_item') { continue }
              $item = $record.payload
              if ($item.type -eq 'function_call' -and
                  ($item.name -replace '^functions\.', '') -eq 'request_user_input_async') {
                try { $questionInput = $item.arguments | ConvertFrom-Json -ErrorAction Stop } catch { continue }
                $questions = @($questionInput.questions)
                if (-not $item.call_id -or -not $questionInput.questions) { continue }
                $remaining = New-Object 'System.Collections.Generic.HashSet[int]'
                for ($i = 0; $i -lt $questions.Count; $i++) { [void]$remaining.Add($i) }
                $pending[$item.call_id] = $remaining
              } elseif ($item.type -eq 'message' -and $item.role -in @('user', 'developer')) {
                $text = ($item.content | ForEach-Object { if ($_.text -is [string]) { $_.text } }) -join "`n"
                $text = $text.Trim()
                if ($item.role -eq 'developer') {
                  if ($text.StartsWith('<turn_aborted>', [StringComparison]::Ordinal)) { $pending.Clear() }
                  continue
                }
                if ($text.StartsWith('<environment_context>', [StringComparison]::Ordinal) -or
                    $text.StartsWith('<user_action>', [StringComparison]::Ordinal) -or
                    $text.StartsWith('<recommended_plugins>', [StringComparison]::Ordinal) -or
                    $text.StartsWith('# AGENTS.md instructions for ', [StringComparison]::Ordinal) -or
                    $text -cmatch '\AChat from [^:\r\n]{1,40}: you have inbox mail from ') { continue }
                $opening = '<send_user_message_question_reply>'
                $closing = '</send_user_message_question_reply>'
                if (-not $text.StartsWith($opening, [StringComparison]::Ordinal) -or
                    -not $text.EndsWith($closing, [StringComparison]::Ordinal)) {
                  # Ordinary user input or <turn_aborted> cancels old questions. Context/mail above
                  # does not. A structured reply below clears only its answered question indexes.
                  $pending.Clear()
                  continue
                }
                $reply = $text.Substring($opening.Length, $text.Length - $opening.Length - $closing.Length).Trim()
                try { $answers = $reply | ConvertFrom-Json -ErrorAction Stop } catch { continue }
                foreach ($answer in @($answers)) {
                  try { $id = $answer.questionItemId | ConvertFrom-Json -ErrorAction Stop } catch { continue }
                  if ($id.Count -ne 3 -or ($id[0] -replace '^functions\.', '') -ne 'request_user_input_async') { continue }
                  $call = [string]$id[1]
                  $index = 0
                  if (-not [int]::TryParse([string]$id[2], [ref]$index) -or -not $pending.ContainsKey($call)) { continue }
                  [void]$pending[$call].Remove($index)
                  if ($pending[$call].Count -eq 0) { $pending.Remove($call) }
                }
              }
            }
          } catch { return $false } finally { if ($reader) { $reader.Dispose() } }
          return $pending.Count -gt 0
        }
        """;
}
