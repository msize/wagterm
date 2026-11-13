# Build the wagterm installer: installer\Output\wagterm-setup-<ver>.exe
# (Wagterm.Win32 + wagtermctl + the Rust pty-host and core dll).
#
# Prereqs: .NET SDK (net10.0-windows) + Inno Setup 6 (ISCC on PATH or the usual locations).
$ErrorActionPreference = "Stop"
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root   = Split-Path -Parent $here
$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }
$stage = Join-Path $here "stage"

# resolve ISCC (Inno Setup compiler): PATH, machine-wide (choco/CI), and the per-user winget location.
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "ISCC (Inno Setup compiler) not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)." }

Write-Host "== clean stage ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

# version = the installer's AppVersion define (stamped into the assemblies so `ping` reports it).
$issText = Get-Content (Join-Path $here "wagterm.iss") -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in wagterm.iss" }
$ver = $Matches[1]

# Self-contained folder publish (robust for Vortice native libs + the on-disk themes\/assets\).
$common = @("-c","Release","-r","win-x64","--self-contained","true","-p:PublishSingleFile=false","-p:Version=$ver","-o",$stage)

Write-Host "== publish Wagterm.Win32 (app) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Wagterm.Win32\Wagterm.Win32.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

Write-Host "== publish Wagterm.Ctl (wagtermctl) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Wagterm.Ctl\Wagterm.Ctl.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "ctl publish failed" }

# Rust emulator core + pty-host: built once, copied into BOTH stages.
Write-Host "== build wagterm-core + pty-host (Rust) ==" -ForegroundColor Cyan
& cargo build --release --manifest-path (Join-Path $root "native\Cargo.toml")
if ($LASTEXITCODE -ne 0) { throw "cargo build (native) failed" }
$coreDll = Join-Path $root "native\target\release\wagterm_core.dll"
$ptyExe  = Join-Path $root "native\target\release\wagterm-ptyhost.exe"
Copy-Item $coreDll $stage -Force
Copy-Item $ptyExe  $stage -Force
# The app loads the bundled Meslo Nerd Font by name from its executable directory.
Copy-Item (Join-Path $root "assets\fonts\MesloLGLDZNerdFont-Regular.ttf") $stage -Force
Copy-Item (Join-Path $root "assets\fonts\FONT-LICENSE.txt") $stage -Force

# sanity: required payload present
foreach ($f in @("wagterm.exe","wagtermctl.exe","wagterm_core.dll","wagterm-ptyhost.exe","conpty.dll","x64\OpenConsole.exe","MesloLGLDZNerdFont-Regular.ttf","assets\wagterm.ico")) {
  if (-not (Test-Path (Join-Path $stage $f))) { throw "stage missing $f" }
}
if (-not (Get-ChildItem (Join-Path $stage "themes") -Filter *.conf -ErrorAction SilentlyContinue)) { throw "stage missing themes\*.conf" }
Write-Host ("stage OK: {0} files" -f (Get-ChildItem $stage -Recurse -File).Count) -ForegroundColor Green

Write-Host "== compile installer (ISCC) ==" -ForegroundColor Cyan
& $iscc (Join-Path $here "wagterm.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem (Join-Path $here "Output") -Filter *.exe |
  Where-Object { $_.Name -like "*$ver*" } | Sort-Object LastWriteTime -Descending |
  ForEach-Object { Write-Host ("== done: {0} ({1:N1} MB) ==" -f $_.FullName, ($_.Length/1MB)) -ForegroundColor Green }
