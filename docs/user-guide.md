# WagTerm user guide

## Agent-first

- **Workspaces → sessions → panes** in a custom-drawn sidebar: drag-reorder, rename, flag, focus,
  unread badges (green `ok`, yellow `normal`, red `attention` or untyped; colors can be changed in Settings → Notifications), **multi-select with Ctrl/Shift+click** for batch flag / move / close, and reopen
  closed sessions *and workspaces* with `Ctrl+Shift+R`. See [workspace navigation](navigation.md).
- **Session link**: give a session the URL of the tracker issue or pull request it is about (any
  tracker, a full `http(s)` URL) from the row's context menu (**Set Link…**), **File ▸ Session
  Link ▸ Set Session Link…**, the action palette or `wagtermctl session link <url>`. The row shows a link glyph left of
  the name (after the flag when both are set); click it, press `Ctrl+Shift+L` or run
  `wagtermctl session link --open` to open the URL in your browser. Hovering the glyph shows the
  URL, `Ctrl+P` finds a session by it, and custom commands get it as `{WAG_LINK}`. The inline field
  starts with the clipboard when it holds a link; an empty field clears it.
- **Dashboard** (`Ctrl+Shift+D`): a grid of **live** session previews — arrow-navigate, Enter or
  double-click to jump in; or drive it with `wagtermctl dashboard <ids>`.
- **Agent status** per session (idle / active / blocked / completed) as a colored dot and a title-bar
  bell, driven by your agent via hooks or the control API, with blink, auto-reset, and sounds. Run
  `wagtermctl install hooks` (or the palette entry) once to wire Claude Code / Codex / Devin up.
  When an existing Devin JSONC config needs changes, the installer keeps its exact original in a
  numbered `.wagterm.bak` sibling, then rewrites the active config as JSON without comments or
  formatting.
- **Agent text notifications**: **Help > Install Agent Notifications** or
  `wagtermctl install notifications` installs notification hooks for Claude Code, Codex and Devin.
  Completion alerts, questions and permission details appear in WagTerm notifications (up to 200 characters).
  Claude Code and Codex include reply previews. Devin shows `Turn completed` when its Stop event
  provides no reply text.
  Claude permission alerts use the same `Notification(permission_prompt)` event as status hooks:
  a generic message after about six seconds without typing. Codex and Devin include tool details.
  Questions and permission requests mark the pane `blocked`; submitting a prompt or completing a
  tool normally restores `active`. Codex async questions retain `blocked` across tool completion
  and Stop until all questions are answered, the turn is cancelled, or ordinary user input supersedes
  them. Injected context and wagloop peer mail do not clear pending questions. Both Codex hooks read
  `transcript_path` for this state. A Codex turn ending with a question also marks it `blocked`.
  After upgrading, rerun whichever of `wagtermctl install hooks` / `wagtermctl install notifications`
  you already use (both if you use both), then restart Codex and enable/trust the updated hooks in `/hooks`.
  This installer is independent of status/resume setup and does not change Codex's `config.toml`
  or its existing `notify` program. It writes `%LOCALAPPDATA%/wagterm/wagterm-agent-notify.ps1`
  and merges hooks into `~/.claude/settings.json`, `~/.codex/hooks.json`, and
  `%APPDATA%/devin/config.json`. Changed configs get a numbered `.wagterm.bak` sibling first.
  Known manual commands of the form `bash <path> stop|permission|question`, using
  `~/.claude/hooks/wagterm-notify.sh` or `~/.config/devin/hooks/wagterm-notify.sh` under their
  matching events, are replaced to avoid duplicate notifications. Scripts and other hooks are kept.
  Devin JSONC is rewritten as JSON, so comments and formatting
  remain only in the backup. **Restart the agents afterwards. In Codex, open `/hooks` and
  enable/trust the new hooks.** No Bash, jq or Python installation is needed by these hooks.
  The installer leaves `desktop-notifications` and Windows notification settings unchanged.
  `desktop-notifications` defaults to `true`, so previews can appear on the desktop immediately.
  Reply and command text can be visible on the desktop: only install if that is appropriate for
  the projects you work on. Hooks stay silent outside WagTerm.

  **Codex Auto-review:** `PermissionRequest` fires before Codex chooses between the automatic
  reviewer and the user. Turn metadata cannot identify mandatory human checkpoints, reviewer
  handoffs, or tool-specific approval settings. The default hook therefore keeps permission alerts.
  To use Codex's actual approval-prompt notifications instead, configure its user `config.toml`:

  ```toml
  [tui]
  notifications = ["approval-requested"]
  notification_method = "osc9"
  notification_condition = "always"
  ```

  Merge these keys into the existing `[tui]` table. Keep only `approval-requested` here to avoid
  duplicates with the completion and question hooks. Then launch Codex from Git Bash with
  `WAGTERM_CODEX_APPROVAL_ALERTS=tui codex` (include your usual Codex arguments). This explicit
  opt-in suppresses only Codex's premature `PermissionRequest` notification hook. Actual shell,
  file-edit, and elicitation approval overlays, including mandatory manual approvals in Auto-review
  mode, emit OSC 9, which WagTerm already displays as a notification and badge. Configure all three keys before
  opting in, otherwise real approval alerts may be missing. Removing the environment variable
  restores hook alerts. The installer does not change these Codex settings automatically.
  Claude and Devin, completion and question hooks, and independent status hooks stay unchanged.
  In this mode, manual approvals set `blocked` only if the separate status hooks are installed
  (`wagtermctl install hooks`). OSC 9 itself does not set the agent status.
  Status hooks can still briefly mark automatic reviews `blocked` until the next tool completes.
  Codex 0.170's separate `request_permissions` overlay emits neither this notification nor a
  `PermissionRequest` hook. That existing limitation affects both alert modes.
- **In-app notification banners** (`wagtermctl notify`, OSC 9/777, agent hooks) stay visible for
  10 seconds by default. Change **Settings → Notifications → In-app banner duration (seconds)**
  or run `wagtermctl config set notification-duration 30` for a 30-second banner. The setting is
  saved and applies to the next notification. The command accepts whole seconds from 1 to 120
  and rejects invalid values. Later notifications or status messages can replace the banner sooner.
- **Claude Code, Codex and Devin session binding & auto-resume**: the same installer adds a
  `SessionStart` hook to each agent. Each time a session starts, resumes, is cleared or compacts, the
  hook tells wagterm the live session id and directory, and wagterm stores the line that resumes it
  in the pane's own shell: `cd '<dir>' && claude --resume <id>` in Git Bash, `Set-Location -LiteralPath
  '<dir>'; codex resume <id>` in PowerShell, `cd /d` in cmd (a directory whose name that shell's quotes
  cannot hold, such as one with `%` or `"` in cmd, is left out and only the resume is stored). Devin
  does not report its directory, so its line changes to Devin's project directory (`DEVIN_PROJECT_DIR`,
  the repository root). The permission or sandbox mode it was started with
  (`--dangerously-skip-permissions`, `--permission-mode`, Codex's `--sandbox`, `--ask-for-approval`,
  `--profile`, Devin's `--permission-mode`, `--sandbox`) is kept. On restart, even after a reboot,
  wagterm types that line into each bound pane and the conversation comes back. A `claude -p`,
  `codex exec` or `devin -p` an agent runs from its own tool shell does not rebind the pane, nor does a
  `devin -p` run in the pane itself. Codex runs a new hook only after you trust it
  once in Codex's `/hooks`, and Codex fires it on a session's first turn. For PowerShell the installer
  also adds a transparent `claude` wrapper (active only inside wagterm) that starts a fresh pane's
  session under the pane id. Already had Claude running before installing this? Run
  `wagtermctl claude adopt` (or palette → *Make Claude Sessions Resumable*) once to bind your
  existing conversations to their panes.
- **Update Claude Code** (palette → *Update Claude Code*, or `wagtermctl claude update`): wagterm
  notices when a new Claude Code ships (npm registry; `claude-update-check = false` to opt out), then,
  on your command, runs `claude update` in an overlay terminal and **restarts every running Claude
  session**, each resuming its own conversation (YOLO panes stay YOLO).
- **WagTerm self-update** (palette → *Update WagTerm*, or `wagtermctl app update`): notices new
  releases on GitHub (`update-check = false` to opt out), then, on your command, downloads the right
  artifact for your install (installer or portable exe), **verifies its SHA-256** against the release
  digest, then asks for confirmation in the WagTerm window to restart **all terminal sessions**.
  The CLI returns an immediate acknowledgement; cancelling the dialog leaves sessions running.
  Running commands and agents stop. In server modes, existing session hosts also exit. Installed
  builds run setup, while portable builds swap their exe. Windows and tabs reopen with fresh
  processes. A failure shows an error after relaunch. If the UI never exited, the helper does not
  launch a second copy; the error appears on the next manual start. Every attempt writes logs
  under `%LOCALAPPDATA%/<app-id>/update-logs` (`wagterm` for the default instance); these logs are
  retained without automatic pruning.
  Scoop and Chocolatey installs are never touched; the
  hint points at your package manager instead.
- **Shells that survive the UI** (EXPERIMENTAL): flip *Settings → General → Session host* to
  **Pty-host server** (`session-host = server`) and your sessions live in a tiny headless process.
  Quit or even crash the UI and every shell (including a running Claude conversation)
  **keeps running**; the next start reattaches each pane to its live session, same process, same
  state. Closing a pane still closes its shell. Self-update explicitly stops these hosts after
  confirmation, so the new version also applies to terminal processes.
- **Splits** — side by side or stacked (`--axis vertical|horizontal`, agterm's words), either pane
  closable, the two swappable with every id kept; a split collapses to the survivor when a pane exits.
- **Scratch** and **quick** terminals ([quick terminal](quick-terminal.md)), ephemeral **overlays**
  over a session or over **one pane** of it (`--pane left|right`; open/close/result/copy/text via the
  [control API](control-api.md)), a passive [session HUD](session-hud.md),
  a [native picker](native-picker.md), and **multi-window** with per-window addressing.

## A real Windows terminal

- **Mouse in TUIs**: mouse-tracking applications receive left clicks, drags and double-clicks.
  Ctrl+click opens hovered links. Hold Shift to select text with WagTerm instead: Shift+double-click
  selects a word, and Shift+click or Shift+drag extends an existing selection.
- **Default Terminal Application**: register wagterm from *Settings → General* (per-user, no admin,
  one-click revert) and every console app you launch — `cmd` from Win+R, double-clicked `.exe`s —
  opens as an wagterm session via the ConPTY handoff, titled by the app.
  See [default-terminal testing](defterm-testing.md).
- **Fast**: sustained output at **~33k lines/s** (~87 % of a bare conhost window), 0.4 s cold start,
  ~0 % idle CPU, and a leak-hunted session lifecycle (`tools/profile-memory.ps1` keeps it honest).
- Sixel and Kitty graphics, win32-input-mode and the Kitty keyboard protocol, SGR and SGR-Pixels mouse
  reporting (`?1006`/`?1016`, including DECRQM discovery and device-pixel coordinates), ligatures
  (toggleable), builtin box-drawing glyphs, buffer restore, block selection and keyboard mark mode,
  read-only panes, elevated and de-elevated sessions side by side (⚡ marker), FTCS/OSC-133 prompt
  marks with jump-to-prompt, taskbar progress (OSC 9;4).
- **Microsoft's ConPTY (opt-in)**: with `conpty = bundled` in `wagterm.conf`, sessions run on the
  `conpty.dll` + `OpenConsole.exe` that ship with wagterm (the ConPTY Windows Terminal uses), not the conhost
  built into Windows. It hands an app's output to wagterm unchanged and passes its queries through, so
  wagterm answers them itself: the theme colors (OSC 10/11, which Codex uses to shade your messages), device
  attributes and the cursor position. It is not the default because it does not repaint after a resize: a pane
  that changes width shows clipped lines and misplaced text until the app redraws. The default, `conpty =
  inbox`, is the built-in conhost. The portable exe carries neither file and always uses it.
- Shells are launched with `TERM_PROGRAM=wagterm` (plus the usual `WAGTERM_*` variables), so prompt
  engines, tmux and scripts can detect the host terminal, and with `COLORTERM=truecolor`, so apps that size
  their palette from the environment use 24-bit color.
- **Git Bash working directory**: PowerShell panes report their directory out of the box. A Git Bash
  (or MSYS2) pane does it with a few lines in `~/.bashrc`, so the title shows the directory and the pane
  restores in it after a restart (wagterm 0.20.11 or later, earlier builds also raised a notification):

  ```bash
  # wagterm: report the working directory at every prompt (OSC 9;9, the form ConPTY passes through)
  if [ "$WAGTERM" = 1 ]; then
    __wag_cwd() { printf '\e]9;9;%s\a' "$(pwd -W)"; }
    PROMPT_COMMAND="__wag_cwd${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
  fi
  ```
- When a **normal profile / login-shell** tab’s process exits, its exit code appears in the session's
  accessible name and unread badge tooltip. A background exit sends an `ok` notification for code 0 or
  `attention` for a non-zero code. The exited shell remains visible; tabs started with
  `session new --command` are unchanged.
- Exited profile/login shells print `Press Enter to close the session.` in the terminal. Enter closes
  that tab without the generic close-confirmation dialog; manual closes still follow
  `confirm-close-session`.

## Accessible — screen readers are first-class

- The terminal is a **UIA text document**: Narrator/NVDA read it line by line, track the caret (tight
  one-cell focus box), and **new output is announced automatically** after it settles.
- **Everything is in the UIA tree**: sessions, every chrome button, settings tabs and controls —
  scannable (Caps Lock + arrows), focusable, and invokable. Dialogs are **modally scoped** so the
  reader cannot wander behind them.
- **F6** moves keyboard focus between the terminal and the session list (arrows + Enter there). The
  **Settings dialog is fully keyboard-navigable** — Tab reaches the tab headers too (Enter switches),
  with a classic keyboard-only dotted focus rectangle; buttons **speak on hover** and show
  **tooltips**.
- **F1 help** lists the *effective* keybindings and, when a reader is attached, speaks an orientation
  guide for low-vision users.

## Looks & feel

- **Whole-window theming** with **~580 bundled themes** (the ghostty / iTerm2 set): sidebar, title
  bar, and terminal retint together. **Fonts apply live** from Settings, and an optional **follow
  Windows light/dark** mode swaps between a light and a dark theme you choose.
- **Configurable sidebar font size**, sidebar tint, window opacity, and inactive-pane muting.
- **cwd in the title** out of the box (composes with oh-my-posh) and an **oh-my-posh theme picker**.
- Toolbar modes (normal / compact / **hidden** full-bleed), window opacity, unfocused dim, per-session
  background watermarks.
- **MRU `Ctrl+Tab` switcher**, fuzzy **command / session / action palettes**, search, tmux-style
  **leader chords**, custom commands with `{WAG_*}` tokens and run modes.

## Menu bar

The title bar carries agterm's menus — **File**, **View**, **Navigate**, **Help** — as a row of
labels after the sidebar toggle. Every row shows its *effective* shortcut (a rebind in `keymap.conf`
shows the rebind), a row that cannot apply right now is dim, and a state row's label follows the
state (Hide Sidebar / Show Sidebar, Flag / Unflag Session). Two rows open a flyout: **File ▸ Open
Window** (the window library, a check mark on the open ones) and **File ▸ Open Recent** (closed
sessions and workspaces).

It takes the Windows keyboard model: a lone **Alt** tap or **F10** focuses the bar (←/→ move, ↓ or
Enter opens, **F / V / N / H** open that menu, Esc leaves), and inside an open menu ←/→ or
**Alt+F / Alt+V / Alt+N / Alt+H** switch menus. From the terminal, Alt+letter goes to the program in
the pane (Claude Code pastes an image on Alt+V), so **Alt, V** rather than Alt+V opens View; from the
session list (F6), the dashboard or the find bar Alt+F / Alt+V / Alt+N / Alt+H open a menu directly
unless `keymap.conf` binds them. The bar is a UIA menu bar, so a screen reader reads and runs it. `show-menu-bar = false` in `wagterm.conf` (or
`wagtermctl config set show-menu-bar false`) removes it; the hidden toolbar mode shows no chrome
and so no bar.

agterm's items that have no wagterm counterpart are left out rather than invented: Edit / Reload
Hooks, Toggle Terminal Zoom, Reset Live Sessions, and the focus-set items (Add Workspace to Focus,
Toggle Workspace Filter, Clear Focus — wagterm's workspace focus is one workspace at a time).

## Keyboard essentials

| Key | Action |
|---|---|
| `F1` | Help (effective keybindings + accessibility guide) |
| `F6` | Move focus terminal ⇄ session list |
| `Ctrl+Shift+D` | Dashboard — grid of live sessions |
| `Ctrl+Shift+T` / `Ctrl+Shift+R` | New session / reopen closed |
| `Ctrl+Shift+L` | Open the session's link in the browser |
| `Ctrl+Tab` | MRU session switcher |
| `Ctrl+D` | Split pane · `` Ctrl+` `` quick terminal · `Ctrl+J` scratch |
| `Ctrl+Shift+P` | Action palette |
| `Alt` (tap) / `F10` | Menu bar · then `F` `V` `N` `H` open a menu |
| `F11` | Fullscreen |

Most built-in actions can be rebound in `keymap.conf` (see `F1` for the live, effective list). Some
gestures are fixed in the app and ignore `unmap`: **F1** (help), **F6** (sidebar focus), **F10** (menu
bar when nothing else bound it), **Ctrl+Shift+D** (dashboard), **Ctrl+`** (quick terminal), and
**Ctrl+= / Ctrl+- / Ctrl+0** (font zoom).

## Configuration

- **`%LOCALAPPDATA%\wagterm\wagterm.conf`** — appearance and behavior (also editable in Settings).
- **`%LOCALAPPDATA%\wagterm\keymap.conf`** — keybindings, custom commands and leader chords.
  On first launch a commented starter is written from the built-in template in
  `Keymap.StarterText` (`src/Wagterm.Core/Keymap.cs`). **`map`** takes one chord, or several
  chords on one line separated by `|` (any count). **`unmap`** drops a default or custom **keymap**
  binding so the chord can fall through to the shell; it does not override the fixed gestures above.
  **Ctrl+Tab** / **Ctrl+Shift+Tab** run the MRU session walk only while the chord is explicitly bound
  to `next_session` or `previous_session`; after `unmap ctrl+tab` MRU does not run and the chord
  follows the normal keymap / terminal path. Use `unmap ctrl+d` for one chord, or `unmap a | b` for
  several. Reload with **File → Reload Keymap** or `wagtermctl keymap reload`.
- `show-menu-bar = false` hides the title-bar menu bar (File ▸ Edit wagterm.conf… opens the file).
- Themes: the bundled set ships with the app; drop extra ghostty-format `*.conf` files in
  `%LOCALAPPDATA%\wagterm\themes\`.
