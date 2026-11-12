# WagTerm installers

Builds **per-user** Windows installers — end users need **no** .NET runtime installed:

- **`wagterm-setup-<ver>.exe`** — the full `wagterm` (.NET) terminal + `wagtermctl`, from a
  self-contained publish. Its shortcuts pass `--default-session-host server-rust` so a fresh
  install defaults new sessions to the Rust pty-host (a first-run seed only; it never overrides
  an existing config, and the Settings UI stays authoritative after).
- **`wagterm-portable-<ver>-win-x64.exe`** — the self-contained single-file app, built separately.

The setup ships `wagterm-ptyhost.exe` and `wagterm_core.dll`. There is no wagterm-lite edition.
Upstream's agliteterm and its historical handover assets are not built or published here.

The wagterm setup also carries Microsoft's ConPTY, `conpty.dll` + `x64\OpenConsole.exe` from the
`Microsoft.Windows.Console.ConPTY` NuGet package (MIT), which `dotnet publish` puts in the stage. Both the app
and `wagterm-ptyhost.exe` load it from their own directory, and fall back to the inbox conhost without it.

## Build

```powershell
installer\build.ps1            # per-user setup (what CI/releases run)
installer\build-portable.ps1   # portable single-file executable
```

Prereqs:
- .NET SDK with the `net10.0-windows` target (the repo pins .NET 10).
- Rust + MSVC C++ build tools (for the Rust core and host).
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) — `ISCC.exe` on PATH, the default install location,
  or the per-user winget location (`winget install JRSoftware.InnoSetup`).

`build.ps1`:
1. publishes `Wagterm.Win32` (the app) and `Wagterm.Ctl` (`wagtermctl`) as **self-contained win-x64**
   (a folder, not single-file — robust for the Vortice native libs and the on-disk `themes\`/`assets\`)
   into `installer\stage`,
2. builds the Rust core and pty-host, staging their binaries and the bundled font in the same directory,
3. compiles `installer\wagterm.iss`,
4. writes `installer\Output\wagterm-setup-<ver>.exe`.

`build.ps1` recreates its staging directory before publishing, so old agwinterm payloads are not included.

## What the installers do

Deliberately **minimal / non-invasive** (agterm-style): they only copy files and create shortcuts.
They do **not** touch your `PATH`, profile, or config.

- **Per-user, no admin** (`PrivilegesRequired=lowest`), installs to `%LOCALAPPDATA%\Programs\wagterm`.
- **Start-menu** shortcut (**WagTerm**), **desktop** shortcut via a checkbox (task).
- **Launch on finish**: a "Launch …" checkbox (interactive installs).
- **Uninstall** removes the app files. Your `%LOCALAPPDATA%\wagterm` config and sessions remain intact.
- **Independent product**: publisher `msize`, a separate installer AppId and default-terminal CLSID,
  and separate paths, hooks and update feed. No agwinterm profile import or cleanup.

### Integrations are opt-in (from inside the app)

Open the action palette (**Ctrl+Shift+P**) and run the **Install …** entries when you want them:

- **Install Command-Line Tool (PATH)** — adds `wagtermctl` to your user `PATH` (so shells & AI agents can call it).
- **Install Agent Status Hooks** — Claude Code / Codex / Devin / generic-agent status reporting, plus a
  transparent `claude` launcher that binds Claude's session id to the wagterm pane so restart
  auto-resumes the conversation. When an existing Devin JSONC config needs changes, its exact original
  is kept in a numbered `.wagterm.bak` sibling before the active config is rewritten as JSON without
  comments or formatting.
- **Install Agent Skill** — teaches agents to drive wagterm via `wagtermctl`.
- **Install Shell Integration** — a `$PROFILE` OSC-7 hook for live cwd (also works out of the box without this).

Each is reversible / re-runnable and can also be driven headless: `wagtermctl install cli|hooks|skill|shell`.

## Silent install / uninstall

```powershell
wagterm-setup-<ver>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
# uninstall:
"%LOCALAPPDATA%\Programs\wagterm\unins000.exe" /VERYSILENT
```

## Signing (not done)

The installer is **unsigned**, so Windows SmartScreen will warn on first run. To sign, obtain a
code-signing certificate and sign both the app exe and the setup exe, e.g.:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 <file>.exe
```

Sign the published `wagterm.exe`/`wagtermctl.exe` (in `stage\`) **before** compiling, then
sign the produced `wagterm-setup-<ver>.exe`.
