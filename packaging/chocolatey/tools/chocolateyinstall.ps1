$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
    packageName    = 'wagterm'
    fileFullPath   = Join-Path $toolsDir 'wagterm.exe'
    url64bit       = 'https://github.com/msize/wagterm/releases/download/v0.21.3/wagterm-portable-0.21.3-win-x64.exe'
    checksum64     = '6f47682fe65e23aa7952be717e7e5383bcefd28646e8b3f97881e46f5934234b'
    checksumType64 = 'sha256'
}

# Portable single-file build: download the exe into the package's tools dir. Chocolatey then
# auto-generates a shim named `wagterm` on PATH; the .gui marker keeps the shim from blocking
# the console. Settings live under %LOCALAPPDATA%\wagterm regardless of where the exe sits.
Get-ChocolateyWebFile @packageArgs
