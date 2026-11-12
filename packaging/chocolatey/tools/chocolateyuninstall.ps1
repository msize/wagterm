$ErrorActionPreference = 'Stop'

# The portable exe was downloaded into the package's tools dir, which Chocolatey deletes on
# uninstall together with the auto-generated shim, so nothing extra is required here. User
# settings under %LOCALAPPDATA%\wagterm are intentionally left in place.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$exe = Join-Path $toolsDir 'wagterm.exe'
if (Test-Path $exe) { Remove-Item $exe -Force -ErrorAction SilentlyContinue }
