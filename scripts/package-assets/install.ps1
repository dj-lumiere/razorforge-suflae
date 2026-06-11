# RazorForge installer (Windows)
#
# Adds this package directory to your user PATH so `razorforge` and `rf`
# work from any terminal. Everything runs from this folder — no admin
# rights, no registry beyond the user PATH entry, no other downloads.
#
# Usage:  pwsh -File install.ps1     (or right-click -> Run with PowerShell)
$ErrorActionPreference = 'Stop'

$dir = $PSScriptRoot
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$entries = $userPath -split ';' | Where-Object { $_ -ne '' }

if ($entries -contains $dir) {
    Write-Host "Already installed: $dir is on your user PATH."
} else {
    [Environment]::SetEnvironmentVariable('Path', (($entries + $dir) -join ';'), 'User')
    Write-Host "Added to user PATH: $dir"
    Write-Host 'Open a NEW terminal for the change to take effect.'
}

Write-Host ''
Write-Host 'Try it:'
Write-Host '  razorforge version'
Write-Host '  razorforge buildandrun hello.rf      (rf works too)'
Write-Host ''
Write-Host 'See QUICKSTART.md for a hello-world walkthrough.'