namespace Wagterm.Pty;

public static partial class AgentNotifications
{
    public const string Script =
        """
        param([string]$Agent)
        # Shared Claude/Codex/Devin notification hook. Stdout is reserved for hook decisions.
        if (-not $env:WAGTERM_SESSION_ID) { exit 0 }
        $ErrorActionPreference = 'Stop'
        try {
          $o = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), [Text.Encoding]::UTF8).ReadToEnd() | ConvertFrom-Json
          if (-not $o -or $Agent -notin @('claude', 'codex', 'devin')) { exit 0 }
          # Opt in only after configuring Codex's actual approval-prompt OSC 9 notifications.
          # PermissionRequest precedes reviewer routing and cannot identify a human wait.
          if ($Agent -eq 'codex' -and $o.hook_event_name -eq 'PermissionRequest' -and
              $env:WAGTERM_CODEX_APPROVAL_ALERTS -ceq 'tui') { exit 0 }
          if ($Agent -eq 'claude') {
            # Devin imports Claude's hooks too; nested print runs also inherit the pane identity.
            if ($env:DEVIN_PROJECT_DIR) { exit 0 }
            if (($env:CLAUDE_CODE_ENTRYPOINT -and $env:CLAUDE_CODE_ENTRYPOINT -ne 'cli') -or $env:CLAUDE_CODE_SESSION_ATTENDED -eq '0') { exit 0 }
          }
          try {
            if ($Agent -eq 'codex' -and $o.transcript_path -and (Test-Path -LiteralPath $o.transcript_path)) {
              $meta = Get-Content -LiteralPath $o.transcript_path -TotalCount 1 -Encoding UTF8 | ConvertFrom-Json
              if ($meta.payload.source -and $meta.payload.source -ne 'cli') { exit 0 }
            }
          } catch { }
          function Text($value) {
            if ($value -is [string]) { return $value }
            return ''
          }
          function Short($value) {
            $text = [regex]::Replace((Text $value), '\x1b\[[0-?]*[ -/]*[@-~]', '')
            $text = [regex]::Replace($text, '[\s\p{Cc}]+', ' ').Trim()
            if ($text.Length -gt 200) {
              $length = 199
              if ([char]::IsHighSurrogate($text[$length - 1])) { $length-- }
              $text = $text.Substring(0, $length) + [char]0x2026
            }
            return $text
          }
          $pipe = if ($env:WAGTERM_PIPE) { $env:WAGTERM_PIPE -replace '^\\\\\.\\pipe\\', '' } else { 'wagterm' }
          function Push($command, $arguments) {
            $c = $null
            try {
              $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
              $c.Connect(1000)
              $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
              $req = @{ cmd = $command; target = $env:WAGTERM_SESSION_ID; args = $arguments }
              if ($env:WAGTERM_WINDOW_ID) { $req.window = $env:WAGTERM_WINDOW_ID }
              $w.WriteLine(($req | ConvertTo-Json -Compress -Depth 10))
              $null = (New-Object System.IO.StreamReader($c)).ReadLineAsync().Wait(1000)
            } catch { } finally { if ($c) { $c.Dispose() } }
          }
          $name = switch ($Agent) { 'claude' { 'Claude' }; 'codex' { 'Codex' }; 'devin' { 'Devin' } }
          $cwd = Text $o.cwd
          if (-not $cwd -and $Agent -eq 'devin') { $cwd = Text $env:DEVIN_PROJECT_DIR }
          $project = Short (($cwd.TrimEnd([char[]]'\/') -split '[\\/]')[-1])
          $suffix = if ($project) { " ($project)" } else { '' }
          $body = ''; $title = ''; $category = 'attention'; $state = ''
          switch ($o.hook_event_name) {
            'Stop' {
              $body = Short $o.last_assistant_message
              if (-not $body) { $body = 'Turn completed' }
              $title = "$name finished$suffix"; $category = 'ok'; $state = 'completed'
              if ($Agent -eq 'codex' -and (Text $o.last_assistant_message).TrimEnd().EndsWith('?')) {
                $title = "$name needs your answer$suffix"; $category = 'attention'; $state = 'blocked'
              }
            }
            'PermissionRequest' {
              $detail = Text $o.tool_input.command
              if (-not $detail) { $detail = Text $o.tool_input.file_path }
              if (-not $detail) { $detail = Text $o.tool_input.path }
              if (-not $detail) { $detail = Text $o.tool_input.cmd }
              if (-not $detail -and $o.tool_input.cmd -is [array]) {
                $detail = ($o.tool_input.cmd | Where-Object { $_ -is [string] }) -join ' '
              }
              $body = Short ((@((Text $o.tool_name), $detail) | Where-Object { $_ }) -join ': ')
              if (-not $body) { $body = 'Waiting for approval' }
              $title = "$name needs permission$suffix"; $state = 'blocked'
            }
            'Notification' {
              if ($Agent -ne 'claude' -or $o.notification_type -ne 'permission_prompt') { exit 0 }
              $body = Short $o.message
              if (-not $body) { $body = 'Waiting for approval' }
              $title = "$name needs permission$suffix"; $state = 'blocked'
            }
            'PreToolUse' {
              if ($o.tool_name -notin @('AskUserQuestion', 'ask_user_question', 'request_user_input')) { exit 0 }
              $questions = @($o.tool_input.questions)
              if ($questions.Count -gt 0) { $body = Short $questions[0].question }
              if (-not $body) { $body = 'Waiting for your answer' }
              $title = "$name asks you to choose$suffix"; $state = 'blocked'
            }
            'UserPromptSubmit' { Push 'session.status' @{ status = 'active' }; exit 0 }
            'PostToolUse' { Push 'session.status' @{ status = 'active' }; exit 0 }
            default { exit 0 }
          }
          Push 'session.status' @{ status = $state }
          Push 'notify' @{ title = $title; body = $body; category = $category }
        } catch { }
        exit 0
        """;
}
